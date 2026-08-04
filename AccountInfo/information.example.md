# OracleHost — AccountInfo template

This is a **sanitized example** of the `AccountInfo/information.md` format. It is safe
to commit and share — every value below is a placeholder.

To use the `AccountInfo` folder with OracleHost:

1. Copy this file to `information.md` (same folder).
2. Replace each `<your-value>` with your real OCI details.
3. Add your private key PEM next to it (filename must NOT contain "public").
4. Do **not** commit `information.md` or the key — the whole `AccountInfo/` folder
   except this example file is gitignored.

```
Tenancy OCID
ocid1.tenancy.oc1..<your-value>

Home region
IAD

User OCID
ocid1.user.oc1..<your-value>

Compartment OCID
ocid1.compartment.oc1..<your-value>

Subnet OCID
ocid1.subnet.oc1..<your-value>

Fingerprint
aa:bb:cc:dd:ee:ff:00:11:22:33:44:55:66:77:88:99
```

| Field | Required? | Notes |
|---|---|---|
| `Tenancy OCID` | ✅ yes | |
| `User OCID` | ✅ yes | |
| `Compartment OCID` | ✅ yes | |
| `Subnet OCID` | ✅ yes | |
| `Home region` | no | e.g. `IAD`. Maps to a region code for the API |
| `Fingerprint` | no | If present, OracleHost verifies it matches the key |
