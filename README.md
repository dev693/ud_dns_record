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
| `-mail`   | yes\*               | united-domains account e-mail.                                          |
| `-pw`     | yes\*               | united-domains account password.                                       |
| `-record` | yes                 | Full TXT record name, e.g. `_acme-challenge.example.com`.               |
| `-value`  | yes for `create`    | TXT content (the ACME token). For `delete` it narrows which record goes. |
| `-tfa`    | only with 2FA       | TOTP secret (base32) if the account has two-factor authentication.      |

\* `-mail`, `-pw` and `-tfa` are optional on the command line if the corresponding
**environment variable** is set instead (see below).

### Environment variables

To keep credentials out of the command line (and out of the win-acme renewal store), the
account credentials can be supplied via environment variables instead of `-mail`/`-pw`/`-tfa`:

| Variable      | Replaces | Description                                |
|---------------|----------|--------------------------------------------|
| `UD_MAIL`     | `-mail`  | united-domains account e-mail.            |
| `UD_PASSWORD` | `-pw`    | united-domains account password.          |
| `UD_TFA`      | `-tfa`   | TOTP secret (base32), only if 2FA is on.  |

A command-line argument always takes precedence over the matching environment variable.
With the variables set, the win-acme arguments reduce to just `-mode`, `-record` and `-value`.

Exit code is `0` on success and non-zero on failure, so win-acme can react to the result.
Both `create` and `delete` are idempotent (creating an existing record or deleting a missing
one is a no-op that still returns `0`).

## win-acme configuration

Use the **`script` DNS validation plugin** and point both the create and delete script at
this executable. Set `UD_MAIL`, `UD_PASSWORD` (and `UD_TFA` if the account uses 2FA) as
environment variables for the win-acme process so credentials stay out of the renewal
configuration. The script arguments then only carry the operation and the values win-acme
substitutes (`{RecordName}` and `{Token}`):

- **Create script:** `ud_dns_record.exe`
  Arguments: `-mode create -record {RecordName} -value {Token}`
- **Delete script:** `ud_dns_record.exe`
  Arguments: `-mode delete -record {RecordName} -value {Token}`

Unattended one-liner (with `UD_MAIL`/`UD_PASSWORD`/`UD_TFA` set in the environment):

```
wacs.exe --target manual --host example.com,*.example.com ^
  --validationmode dns-01 --validation script ^
  --dnscreatescript "C:\path\ud_dns_record.exe" ^
  --dnscreatescriptarguments "-mode create -record {RecordName} -value {Token}" ^
  --dnsdeletescript "C:\path\ud_dns_record.exe" ^
  --dnsdeletescriptarguments "-mode delete -record {RecordName} -value {Token}"
```

If you prefer, you can still pass `-mail`/`-pw`/`-tfa` directly as script arguments instead of
using the environment variables.

> **Security note:** prefer the environment variables — credentials passed as script arguments
> are stored in the win-acme renewal configuration and may appear in process listings. Either
> way, use a dedicated united-domains account with the least privileges necessary and protect
> the win-acme `settings`/renewal store accordingly.

## Build

```
dotnet build ud_dns_record.sln -c Release
```
