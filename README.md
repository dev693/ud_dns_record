# ud_dns_record

A small .NET 9 console tool that logs into [united-domains](https://www.united-domains.de/)
and **creates or deletes a TXT DNS record**. It is built to act as the DNS-01 validation
hook for [win-acme (wacs)](https://www.win-acme.com/) so you can issue **wildcard**
certificates (`*.example.com`) for domains hosted at united-domains.

During certificate issuance win-acme calls this tool twice:

1. **create** – publish `_acme-challenge.<domain>` with the ACME challenge token, then wait
   for DNS propagation and let the ACME server validate it.
2. **delete** – remove the `_acme-challenge.<domain>` record again once validation is done.

## Usage

```
ud_dns_record.exe -mode create -mail <mail> -pw <password> [-tfa <secret>] -record <_acme-challenge.example.com> -value <txt-value>
ud_dns_record.exe -mode delete -mail <mail> -pw <password> [-tfa <secret>] -record <_acme-challenge.example.com> [-value <txt-value>]
```

| Argument  | Required            | Description                                                              |
|-----------|---------------------|--------------------------------------------------------------------------|
| `-mode`   | yes                 | `create` or `delete`.                                                     |
| `-mail`   | yes                 | united-domains account e-mail.                                           |
| `-pw`     | yes                 | united-domains account password.                                        |
| `-record` | yes                 | Full TXT record name, e.g. `_acme-challenge.example.com`.               |
| `-value`  | yes for `create`    | TXT content (the ACME token). For `delete` it narrows which record goes. |
| `-tfa`    | only with 2FA       | TOTP secret (base32) if the account has two-factor authentication.      |

Exit code is `0` on success and non-zero on failure, so win-acme can react to the result.
Both `create` and `delete` are idempotent (creating an existing record or deleting a missing
one is a no-op that still returns `0`).

## win-acme configuration

Use the **`script` DNS validation plugin** and point both the create and delete script at
this executable. Example arguments (win-acme substitutes `{RecordName}` and `{Token}`):

- **Create script:** `ud_dns_record.exe`
  Arguments: `-mode create -mail you@example.com -pw secret -tfa BASE32SECRET -record {RecordName} -value {Token}`
- **Delete script:** `ud_dns_record.exe`
  Arguments: `-mode delete -mail you@example.com -pw secret -tfa BASE32SECRET -record {RecordName} -value {Token}`

Unattended one-liner:

```
wacs.exe --target manual --host example.com,*.example.com ^
  --validationmode dns-01 --validation script ^
  --dnscreatescript "C:\path\ud_dns_record.exe" ^
  --dnscreatescriptarguments "-mode create -mail you@example.com -pw secret -tfa BASE32SECRET -record {RecordName} -value {Token}" ^
  --dnsdeletescript "C:\path\ud_dns_record.exe" ^
  --dnsdeletescriptarguments "-mode delete -mail you@example.com -pw secret -tfa BASE32SECRET -record {RecordName} -value {Token}"
```

> **Security note:** credentials are passed on the command line and are therefore stored in
> the win-acme renewal configuration and may appear in process listings. Use a dedicated
> united-domains account with the least privileges necessary, and protect the win-acme
> `settings`/renewal store accordingly.

## Build

```
dotnet build ud_dns_record.sln -c Release
```
