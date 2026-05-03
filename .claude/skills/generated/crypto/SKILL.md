---
name: crypto
description: "Skill for the Crypto area of RemEx. 9 symbols across 4 files."
---

# Crypto

9 symbols | 4 files | Cohesion: 100%

## When to Use

- Working with code in `RemEx.Android/`
- Understanding how checkClientTrusted, checkServerTrusted, verifyRemoteCertificate work
- Modifying crypto-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `RemEx.Android/app/src/main/java/net/dot/android/crypto/DotnetProxyTrustManager.java` | checkClientTrusted, checkServerTrusted, verifyRemoteCertificate |
| `RemEx.Android/app/src/main/java/net/dot/android/crypto/PalPbkdf2.java` | pbkdf2OneShot, writeBigEndianInt |
| `RemEx.Android/app/src/main/java/net/dot/android/crypto/DotnetProxyX509TrustManager.java` | checkServerTrusted, verifyRemoteCertificate |
| `RemEx.Android/app/src/main/java/net/dot/android/crypto/DotnetProxyTrustManager_X509TrustManager.java` | checkServerTrusted, verifyRemoteCertificate |

## Entry Points

Start here when exploring this area:

- **`checkClientTrusted`** (Method) — `RemEx.Android/app/src/main/java/net/dot/android/crypto/DotnetProxyTrustManager.java:14`
- **`checkServerTrusted`** (Method) — `RemEx.Android/app/src/main/java/net/dot/android/crypto/DotnetProxyTrustManager.java:21`
- **`verifyRemoteCertificate`** (Method) — `RemEx.Android/app/src/main/java/net/dot/android/crypto/DotnetProxyTrustManager.java:33`
- **`pbkdf2OneShot`** (Method) — `RemEx.Android/app/src/main/java/net/dot/android/crypto/PalPbkdf2.java:17`
- **`writeBigEndianInt`** (Method) — `RemEx.Android/app/src/main/java/net/dot/android/crypto/PalPbkdf2.java:79`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `checkClientTrusted` | Method | `RemEx.Android/app/src/main/java/net/dot/android/crypto/DotnetProxyTrustManager.java` | 14 |
| `checkServerTrusted` | Method | `RemEx.Android/app/src/main/java/net/dot/android/crypto/DotnetProxyTrustManager.java` | 21 |
| `verifyRemoteCertificate` | Method | `RemEx.Android/app/src/main/java/net/dot/android/crypto/DotnetProxyTrustManager.java` | 33 |
| `pbkdf2OneShot` | Method | `RemEx.Android/app/src/main/java/net/dot/android/crypto/PalPbkdf2.java` | 17 |
| `writeBigEndianInt` | Method | `RemEx.Android/app/src/main/java/net/dot/android/crypto/PalPbkdf2.java` | 79 |
| `checkServerTrusted` | Method | `RemEx.Android/app/src/main/java/net/dot/android/crypto/DotnetProxyX509TrustManager.java` | 18 |
| `verifyRemoteCertificate` | Method | `RemEx.Android/app/src/main/java/net/dot/android/crypto/DotnetProxyX509TrustManager.java` | 30 |
| `checkServerTrusted` | Method | `RemEx.Android/app/src/main/java/net/dot/android/crypto/DotnetProxyTrustManager_X509TrustManager.java` | 18 |
| `verifyRemoteCertificate` | Method | `RemEx.Android/app/src/main/java/net/dot/android/crypto/DotnetProxyTrustManager_X509TrustManager.java` | 30 |

## How to Explore

1. `gitnexus_context({name: "checkClientTrusted"})` — see callers and callees
2. `gitnexus_query({query: "crypto"})` — find related execution flows
3. Read key files listed above for implementation details
