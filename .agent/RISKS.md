# RISKS

- R-001 [MEDIUM] Word path unverified locally: LibreOffice not installed on the dev PC. Covered by `files.yml` once pushed (Q-001).
- R-002 [MEDIUM] Engines run with the API's OS identity; no OS-level sandbox (seccomp/AppContainer) yet. Mitigated by memory-safe Rust decoding, size/pixel/page limits, timeouts, stripped env. Follow-up: run engines in a container or low-privilege account.
- R-003 [LOW] In-memory job index: results are lost on restart and not shared across instances. Single instance only (documented behaviour).
- R-004 [LOW] PDF to Word is layout-approximate (paragraphs, fonts, pictures); tables and multi-column layouts are not rebuilt; scanned pages become pictures (no OCR).
- R-005 [LOW] WebP output is lossless only (pure-Rust encoder); lossy small files should use JPEG.
- R-006 [LOW] Upscale model is a third-party ONNX export; integrity enforced by SHA-256, provenance not cryptographically signed.
- R-007 [LOW] Anonymous API: protected by per-IP rate limit and size limits only; add auth if exposed publicly at scale.

### R-008 [LOW] Word tool pages don't check /v1/capabilities
- Without LibreOffice on the server, Word tools only fail after upload. Fix: tool.js reads capabilities and disables Run with a notice.

