//! Versioned user-bound DPAPI storage. Legacy pyatv files are never modified.
use crate::auth::Credentials;
use anyhow::{Context, Result, ensure};
use sha2::{Digest, Sha256};
use std::{
    io::Write,
    path::{Path, PathBuf},
};
use windows::{
    Win32::{
        Foundation::{HLOCAL, LocalFree},
        Security::Cryptography::*,
        Storage::FileSystem::{MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW},
    },
    core::{HSTRING, w},
};
use zeroize::Zeroizing;
const MAGIC: &[u8] = b"W2AP\x01";
pub fn directory() -> PathBuf {
    std::env::var_os("APPDATA")
        .map(PathBuf::from)
        .unwrap_or_default()
        .join("AirFlash/native-credentials")
}
pub fn path(root: &Path, id: &str) -> PathBuf {
    let normalized = id.replace(':', "").to_ascii_lowercase();
    let hash = Sha256::digest(normalized.as_bytes());
    root.join(format!(
        "{}.dpapi",
        hash.iter().map(|b| format!("{b:02x}")).collect::<String>()
    ))
}
fn protect(input: &[u8], encrypt: bool) -> Result<Zeroizing<Vec<u8>>> {
    let blob = CRYPT_INTEGER_BLOB {
        cbData: input.len().try_into()?,
        pbData: input.as_ptr() as *mut u8,
    };
    let entropy = b"AirFlash HAP credentials v1";
    let entropy = CRYPT_INTEGER_BLOB {
        cbData: entropy.len() as u32,
        pbData: entropy.as_ptr() as *mut u8,
    };
    let mut output = CRYPT_INTEGER_BLOB::default();
    unsafe {
        if encrypt {
            CryptProtectData(
                &blob,
                w!("AirFlash"),
                Some(&entropy),
                None,
                None,
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut output,
            )?;
        } else {
            CryptUnprotectData(
                &blob,
                None,
                Some(&entropy),
                None,
                None,
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut output,
            )?;
        }
        let data = Zeroizing::new(
            std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec(),
        );
        if !encrypt {
            std::ptr::write_bytes(output.pbData, 0, output.cbData as usize);
        }
        LocalFree(Some(HLOCAL(output.pbData.cast())));
        Ok(data)
    }
}
pub fn save(root: &Path, id: &str, credentials: &Credentials) -> Result<()> {
    std::fs::create_dir_all(root)?;
    let clear = Zeroizing::new(serde_json::to_vec(credentials)?);
    let encrypted = protect(&clear, true)?;
    let target = path(root, id);
    let temp = root.join(format!("{}.tmp", uuid::Uuid::new_v4()));
    let result = (|| -> Result<()> {
        let mut file = std::fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temp)?;
        file.write_all(MAGIC)?;
        file.write_all(&encrypted)?;
        file.sync_all()?;
        drop(file);
        unsafe {
            MoveFileExW(
                &HSTRING::from(temp.as_os_str()),
                &HSTRING::from(target.as_os_str()),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
            )
        }?;
        Ok(())
    })();
    if result.is_err() {
        let _ = std::fs::remove_file(&temp);
    }
    result
}
pub fn load(root: &Path, id: &str) -> Result<Option<Credentials>> {
    let target = path(root, id);
    if !target.exists() {
        return Ok(None);
    }
    let metadata = std::fs::metadata(&target)?;
    ensure!(metadata.len() <= 16384, "credential file too large");
    let data = std::fs::read(target)?;
    ensure!(data.starts_with(MAGIC), "unsupported credential format");
    let clear = protect(&data[MAGIC.len()..], false)
        .context("cannot decrypt native credentials for this Windows user")?;
    let credentials: Credentials = serde_json::from_slice(&clear)?;
    ensure!(
        !credentials.controller_id.is_empty() && !credentials.accessory_id.is_empty(),
        "empty credential identity"
    );
    Ok(Some(credentials))
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn dpapi_roundtrip_and_tamper() {
        let original = b"synthetic-test-key";
        let protected = protect(original, true).unwrap();
        assert_eq!(protect(&protected, false).unwrap().as_slice(), original);
        let mut damaged = protected.to_vec();
        let end = damaged.len() - 1;
        damaged[end] ^= 1;
        assert!(protect(&damaged, false).is_err());
    }
}
