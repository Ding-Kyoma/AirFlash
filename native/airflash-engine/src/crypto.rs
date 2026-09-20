//! HAP record framing. Cryptographic primitives are provided by RustCrypto.
use anyhow::{Result, bail, ensure};
use chacha20poly1305::{
    ChaCha20Poly1305, KeyInit, Nonce,
    aead::{Aead, Payload},
};
use hkdf::Hkdf;
use sha2::Sha512;
use zeroize::Zeroizing;

pub fn derive(secret: &[u8], salt: &str, info: &str) -> [u8; 32] {
    let mut key = [0; 32];
    Hkdf::<Sha512>::new(Some(salt.as_bytes()), secret)
        .expand(info.as_bytes(), &mut key)
        .expect("valid HKDF output size");
    key
}

pub struct Cipher {
    key: Zeroizing<[u8; 32]>,
    counter: u64,
}
impl Cipher {
    pub fn new(key: [u8; 32]) -> Self {
        Self {
            key: Zeroizing::new(key),
            counter: 0,
        }
    }
    pub fn counter(&self) -> u64 {
        self.counter
    }
    fn nonce(&self) -> [u8; 12] {
        let mut nonce = [0; 12];
        nonce[4..].copy_from_slice(&self.counter.to_le_bytes());
        nonce
    }
    pub fn encrypt(&mut self, data: &[u8], aad: &[u8]) -> Result<Vec<u8>> {
        ensure!(self.counter < u64::MAX, "nonce exhausted");
        let out = ChaCha20Poly1305::new_from_slice(self.key.as_ref())
            .unwrap()
            .encrypt(Nonce::from_slice(&self.nonce()), Payload { msg: data, aad })
            .map_err(|_| anyhow::anyhow!("encryption failed"))?;
        self.counter += 1;
        Ok(out)
    }
    pub fn decrypt(&mut self, data: &[u8], aad: &[u8]) -> Result<Vec<u8>> {
        ensure!(self.counter < u64::MAX, "nonce exhausted");
        let out = ChaCha20Poly1305::new_from_slice(self.key.as_ref())
            .unwrap()
            .decrypt(Nonce::from_slice(&self.nonce()), Payload { msg: data, aad })
            .map_err(|_| anyhow::anyhow!("authentication tag mismatch"))?;
        self.counter += 1;
        Ok(out)
    }
    pub fn records(&mut self, data: &[u8]) -> Result<Vec<u8>> {
        let mut out = Vec::with_capacity(data.len() + 64);
        for block in data.chunks(1024) {
            let length = (block.len() as u16).to_le_bytes();
            out.extend(length);
            out.extend(self.encrypt(block, &length)?);
        }
        Ok(out)
    }
}

pub fn tlv_encode(items: &[(u8, &[u8])]) -> Vec<u8> {
    let mut out = Vec::new();
    for (tag, bytes) in items {
        if bytes.is_empty() {
            out.extend([*tag, 0]);
        }
        for chunk in bytes.chunks(255) {
            out.extend([*tag, chunk.len() as u8]);
            out.extend(chunk);
        }
    }
    out
}
pub fn tlv_decode(bytes: &[u8]) -> Result<std::collections::BTreeMap<u8, Vec<u8>>> {
    let mut out: std::collections::BTreeMap<u8, Vec<u8>> = Default::default();
    let mut pos = 0;
    while pos < bytes.len() {
        ensure!(pos + 2 <= bytes.len(), "truncated TLV header");
        let (tag, len) = (bytes[pos], bytes[pos + 1] as usize);
        pos += 2;
        ensure!(pos + len <= bytes.len(), "truncated TLV value");
        out.entry(tag)
            .or_default()
            .extend_from_slice(&bytes[pos..pos + len]);
        pos += len;
    }
    if let Some(error) = out.get(&7) {
        bail!("pairing rejected (TLV error {error:?})");
    }
    Ok(out)
}
/// Fixed protocol nonces are used only with newly derived pair-setup/verify keys.
pub fn auth_seal(key: &[u8; 32], label: &[u8; 8], data: &[u8]) -> Result<Vec<u8>> {
    let mut nonce = [0; 12];
    nonce[4..].copy_from_slice(label);
    ChaCha20Poly1305::new_from_slice(key)
        .unwrap()
        .encrypt(Nonce::from_slice(&nonce), data)
        .map_err(|_| anyhow::anyhow!("pairing encryption failed"))
}
pub fn auth_open(key: &[u8; 32], label: &[u8; 8], data: &[u8]) -> Result<Vec<u8>> {
    let mut nonce = [0; 12];
    nonce[4..].copy_from_slice(label);
    ChaCha20Poly1305::new_from_slice(key)
        .unwrap()
        .decrypt(Nonce::from_slice(&nonce), data)
        .map_err(|_| anyhow::anyhow!("pairing authentication failed"))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn fragmented_tlv_and_truncation() {
        let bytes = vec![17; 384];
        let encoded = tlv_encode(&[(3, &bytes), (6, &[2])]);
        assert_eq!(tlv_decode(&encoded).unwrap()[&3], bytes);
        assert!(tlv_decode(&encoded[..encoded.len() - 1]).is_err());
        assert!(tlv_decode(&[7, 1, 2]).is_err());
    }
    #[test]
    fn records_authenticate_length_and_sequence() {
        let mut tx = Cipher::new([42; 32]);
        let mut rx = Cipher::new([42; 32]);
        let data = vec![99; 2500];
        let encoded = tx.records(&data).unwrap();
        let mut result = Vec::new();
        let mut pos = 0;
        while pos < encoded.len() {
            let aad = &encoded[pos..pos + 2];
            let n = u16::from_le_bytes(aad.try_into().unwrap()) as usize;
            let block = &encoded[pos + 2..pos + 2 + n + 16];
            result.extend(rx.decrypt(block, aad).unwrap());
            pos += n + 18;
        }
        assert_eq!(result, data);
        assert!(
            Cipher::new([42; 32])
                .decrypt(&encoded[2..1042], &[0, 3])
                .is_err()
        );
        assert!(rx.decrypt(&encoded[2..1042], &encoded[..2]).is_err());
    }
}
