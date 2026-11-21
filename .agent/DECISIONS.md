# DECISIONS

### D-001 Frontend: ASP.NET Core MVC, not MAUI — REVIEW: yes
- Context: owner allowed MVC or MAUI. An ilovepdf-style service is used in browsers on any device.
- Decision: MVC website; the browser calls the API directly (CORS allowlist / same-origin under IIS).
- Alternatives: MAUI (per-platform installs, still needs the API). Consequence: MAUI can be added later on the same API.

### D-002 Engines per job — REVIEW: yes
- Rust `imgcore` for pixel work: pure-Rust codecs are memory-safe on untrusted uploads; SIMD resize.
- Python `docengine` for documents: best permissive ecosystem (pypdfium2 Apache/BSD, pdfminer.six MIT, python-docx MIT, onnxruntime MIT).
- LibreOffice (MPL-2.0) for Word input: no permissive pure-.NET DOCX renderer (DocSharp.Renderer depends on QuestPDF, commercial terms).
- Rejected: PyMuPDF / pdf2docx (AGPL, unsafe for a hosted service), Ghostscript (AGPL), libimagequant (GPL).

### D-003 Clean Architecture split — REVIEW: yes
- Domain / Application / Infrastructure / Api / Web projects. Ports in Application, one adapter per port.
- Conversions are `IConversionRoute` strategies (open/closed: new conversion = new class).

### D-004 Engines as separate processes
- One process per job, JSON over stdin/stdout, stripped environment, timeout kills process tree, global concurrency gate.
- Consequence: crash isolation and a hard boundary between untrusted parsing and the API process; ~100-500 ms startup per Python job.

### D-005 AI upscale model
- Real-ESRGAN general x4v3 ONNX (BSD-3), from Hugging Face `jonathanst29/tinier-upscale-models` at commit 899dc1e, SHA-256 924EBAD6…DF8D5 pinned and verified at startup.
- Third-party export of the official weights; hash pin makes substitution detectable.

### D-006 Storage and retention
- Results on disk per job, 128-bit random ids, 30 min retention, uploads deleted on publish, work dir wiped on start only if it carries the marker file.

### D-007 Repo moved out of OneDrive (local only)
- OneDrive placeholders and sync locks broke reads and cargo writes. Work continued in `C:\Users\rajen\Downloads\DONDAI\Beyond\Commonizty\commonlyz`.

### D-008 Test fix: palette PNG test uses a gradient
- The first version used random noise, where a 256-colour palette cannot be smaller than truecolour PNG. Changed to a photo-like gradient, which is what the feature targets.

### D-009 Push `agent/files-converter` to origin
- Source: owner answer Q-001 (A). Push the agent branch only; no merge to `main`.

### D-010 Smart PNG falls back to lossless when the palette is not smaller
- Found by T-001's tests: smooth gradients compress better as truecolour. `lossy_png` now returns the smaller of palette and lossless, so it can never grow a file.

### D-011 Correction to D-008
- The final palette test uses flat colour regions with xorshift noise (photo-like), plus a separate gradient test asserting smart PNG never grows a file (D-010).
