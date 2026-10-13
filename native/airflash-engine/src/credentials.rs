//! Versioned user-bound DPAPI storage. Legacy pyatv files are never modified.
use crate::auth::Credentials;
use anyhow::{Context, Result, ensure};
use sha2::{Digest, Sha256};
use std::{
    io::{self, Read, Write},
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
const MAX_FILE_BYTES: usize = 16 * 1024;
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
    load_with(&target, |target| std::fs::File::open(target))
}

fn load_with<R: Read>(
    target: &Path,
    open: impl FnOnce(&Path) -> io::Result<R>,
) -> Result<Option<Credentials>> {
    let file = match open(target) {
        Ok(file) => file,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(error).context("cannot open native credentials"),
    };
    // Read the opened object, not the path again. Only the initial open can establish
    // absence; disappearance or other failures during reading must remain errors.
    let mut data = Vec::new();
    file.take((MAX_FILE_BYTES + 1) as u64)
        .read_to_end(&mut data)
        .context("cannot read native credentials")?;
    ensure!(data.len() <= MAX_FILE_BYTES, "credential file too large");
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
pub(crate) fn write_test_payload(root: &Path, id: &str, clear: &[u8]) -> Result<()> {
    let protected = protect(clear, true)?;
    let mut data = MAGIC.to_vec();
    data.extend_from_slice(&protected);
    std::fs::write(path(root, id), data)?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::transport::Fault;
    use std::{cell::Cell, io::Cursor};

    struct TemporaryRoot(PathBuf);
    impl TemporaryRoot {
        fn new() -> Self {
            let root = std::env::temp_dir().join(format!(
                "airflash-credential-tests-{}",
                uuid::Uuid::new_v4()
            ));
            std::fs::create_dir(&root).unwrap();
            Self(root)
        }
    }
    impl Drop for TemporaryRoot {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    fn synthetic_credentials() -> Credentials {
        Credentials {
            accessory_id: b"synthetic-accessory".to_vec(),
            accessory_public: [11; 32],
            controller_id: b"synthetic-controller".to_vec(),
            controller_secret: [22; 32],
        }
    }
    fn expect_error(result: Result<Option<Credentials>>) -> anyhow::Error {
        match result {
            Err(error) => error,
            Ok(_) => panic!("credential load unexpectedly succeeded"),
        }
    }
    fn assert_setup_io_event(
        error: &anyhow::Error,
        kind: io::ErrorKind,
        context: &str,
        original: &str,
    ) {
        assert_eq!(error.downcast_ref::<io::Error>().unwrap().kind(), kind);
        let fault = Fault::from_error("127.0.0.1", "setup", error);
        let serialized = serde_json::to_string(&fault.event()).unwrap();
        let event: serde_json::Value = serde_json::from_str(&serialized).unwrap();
        assert_eq!(event["event"], "error");
        assert_eq!(event["host"], "127.0.0.1");
        assert_eq!(event["channel"], "setup");
        assert_eq!(event["code"], "socket_error");
        assert_eq!(event["retryable"], true);
        let message = event["message"].as_str().unwrap();
        assert!(message.contains(context));
        assert!(message.contains(original));
    }
    #[test]
    fn only_initial_open_not_found_establishes_absence() {
        let target = Path::new("synthetic.dpapi");
        for kind in [
            io::ErrorKind::NotFound,
            io::ErrorKind::PermissionDenied,
            io::ErrorKind::Other,
        ] {
            let opens = Cell::new(0);
            let result = load_with(target, |opened| -> io::Result<Cursor<Vec<u8>>> {
                assert_eq!(opened, target);
                opens.set(opens.get() + 1);
                Err(io::Error::new(kind, "synthetic open failure"))
            });
            assert_eq!(opens.get(), 1);
            if kind == io::ErrorKind::NotFound {
                assert!(result.unwrap().is_none());
            } else {
                let error = expect_error(result);
                assert_setup_io_event(
                    &error,
                    kind,
                    "cannot open native credentials",
                    "synthetic open failure",
                );
            }
        }
    }
    #[test]
    fn read_errors_after_successful_open_never_establish_absence() {
        struct FailingReader {
            prefix: Cursor<Vec<u8>>,
            kind: io::ErrorKind,
        }
        impl Read for FailingReader {
            fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
                if self.prefix.position() < self.prefix.get_ref().len() as u64 {
                    self.prefix.read(buffer)
                } else {
                    Err(io::Error::new(self.kind, "synthetic read failure"))
                }
            }
        }
        for kind in [
            io::ErrorKind::NotFound,
            io::ErrorKind::PermissionDenied,
            io::ErrorKind::Other,
        ] {
            let error = expect_error(load_with(Path::new("synthetic.dpapi"), |_| {
                Ok(FailingReader {
                    prefix: Cursor::new(MAGIC.to_vec()),
                    kind,
                })
            }));
            assert_setup_io_event(
                &error,
                kind,
                "cannot read native credentials",
                "synthetic read failure",
            );
        }
    }
    #[test]
    fn bounded_read_rejects_oversize_without_relying_on_metadata() {
        struct GrowingReader<'a>(&'a Cell<usize>);
        impl Read for GrowingReader<'_> {
            fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
                buffer.fill(0);
                self.0.set(self.0.get() + buffer.len());
                Ok(buffer.len())
            }
        }
        let count = Cell::new(0);
        let error = expect_error(load_with(Path::new("synthetic.dpapi"), |_| {
            Ok(GrowingReader(&count))
        }));
        assert_eq!(count.get(), 16_385);
        assert_eq!(error.to_string(), "credential file too large");
        let error = expect_error(load_with(Path::new("synthetic.dpapi"), |_| {
            Ok(Cursor::new(vec![0; 16_384]))
        }));
        assert_eq!(error.to_string(), "unsupported credential format");
    }
    #[test]
    fn synthetic_saved_credentials_use_normalized_accessory_key_and_replace_existing_file() {
        let root = TemporaryRoot::new();
        assert!(load(&root.0, "synthetic:accessory").unwrap().is_none());
        let mut credentials = synthetic_credentials();
        save(&root.0, "SYNTHETIC:ACCESSORY", &credentials).unwrap();
        let loaded = load(&root.0, "syntheticaccessory").unwrap().unwrap();
        assert_eq!(loaded.accessory_id, credentials.accessory_id);
        assert_eq!(loaded.controller_id, credentials.controller_id);
        assert_eq!(loaded.accessory_public, credentials.accessory_public);
        assert_eq!(loaded.controller_secret, credentials.controller_secret);
        assert!(
            load(&root.0, "another-synthetic-accessory")
                .unwrap()
                .is_none()
        );
        credentials.controller_id = b"replacement-controller".to_vec();
        save(&root.0, "syntheticaccessory", &credentials).unwrap();
        let loaded = load(&root.0, "SYNTHETIC:ACCESSORY").unwrap().unwrap();
        assert_eq!(loaded.controller_id, credentials.controller_id);
        assert_eq!(std::fs::read_dir(&root.0).unwrap().count(), 1);
    }
    #[test]
    fn path_disappearance_after_open_reads_original_handle() {
        let root = TemporaryRoot::new();
        let credentials = synthetic_credentials();
        save(&root.0, "synthetic-accessory", &credentials).unwrap();
        let target = path(&root.0, "synthetic-accessory");
        let loaded = load_with(&target, |opened| {
            let file = std::fs::File::open(opened)?;
            std::fs::remove_file(opened)?;
            Ok(file)
        })
        .unwrap()
        .unwrap();
        assert_eq!(loaded.controller_id, credentials.controller_id);
        assert_eq!(loaded.accessory_id, credentials.accessory_id);
        assert_eq!(
            std::fs::File::open(&target).unwrap_err().kind(),
            io::ErrorKind::NotFound
        );
    }
    #[test]
    fn unsupported_tampered_and_invalid_payloads_remain_errors() {
        let root = TemporaryRoot::new();
        let id = "synthetic-accessory";
        std::fs::write(path(&root.0, id), b"W2AP\x02").unwrap();
        assert_eq!(
            expect_error(load(&root.0, id)).to_string(),
            "unsupported credential format"
        );
        save(&root.0, id, &synthetic_credentials()).unwrap();
        let mut data = std::fs::read(path(&root.0, id)).unwrap();
        let last = data.len() - 1;
        data[last] ^= 1;
        std::fs::write(path(&root.0, id), data).unwrap();
        assert!(
            expect_error(load(&root.0, id))
                .to_string()
                .contains("cannot decrypt native credentials")
        );
        write_test_payload(&root.0, id, b"not-json").unwrap();
        assert!(expect_error(load(&root.0, id)).is::<serde_json::Error>());
        for empty_accessory in [false, true] {
            let mut credentials = synthetic_credentials();
            if empty_accessory {
                credentials.accessory_id.clear();
            } else {
                credentials.controller_id.clear();
            }
            let clear = Zeroizing::new(serde_json::to_vec(&credentials).unwrap());
            write_test_payload(&root.0, id, &clear).unwrap();
            assert_eq!(
                expect_error(load(&root.0, id)).to_string(),
                "empty credential identity"
            );
        }
    }
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
