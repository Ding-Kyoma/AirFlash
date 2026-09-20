use airflash_engine::auth::client_proof;
fn hex(s: &str) -> Vec<u8> {
    (0..s.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
        .collect()
}
#[test]
fn independent_srp_vector() {
    let v: serde_json::Value = serde_json::from_str(include_str!("vectors/srp.json")).unwrap();
    let get = |name: &str| hex(v[name].as_str().unwrap());
    let proof = client_proof(&get("private"), &get("salt"), &get("server_public")).unwrap();
    assert_eq!(proof.public, get("client_public"));
    assert_eq!(proof.proof, get("proof"));
    assert_eq!(proof.expected, get("server_proof"));
    assert_eq!(*proof.key, get("key"));
}
