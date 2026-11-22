# PLAN

Spec hash: none (no `.agent/SPEC.md`; requirements below are the owner's chat requests of 2026-11-17).

## Requirements (owner, chat)
- REQ-1 Converter between Word, PDF and images in both directions, like ilovepdf.com.
- REQ-2 Image compression, resolution, aspect ratio and cropping online.
- REQ-3 Enhance image quality or deliberately decrease it.
- REQ-4 Main backend API in .NET 10; frontend ASP.NET MVC (or MAUI); Python/Rust engines where they are better.
- REQ-5 Clean Architecture + SOLID; secure, reliable and fast.

## Architecture overview (D-001..D-004)
```
Browser ──> Kamala.Web (MVC, static UI, strict CSP)
   └──────> Kamala.Api (/v1, rate limit, CORS allowlist)
              └─ Application (use cases, ports, conversion routes) ─ Domain (rules, validation)
              └─ Infrastructure ─┬─ imgcore (Rust process)
                                 ├─ docengine (Python process: PDFium, pdfminer, python-docx, ONNX Runtime)
                                 ├─ LibreOffice (headless process, locked-down profile)
                                 └─ DiskJobStore (per-job folders, 30 min retention)
```

## Tasks (milestone M1 Files)
| ID | Task | Status | Evidence |
|---|---|---|---|
| T-001 | Rust imgcore: ops + encoders + limits | done | cargo test; smoke_imgcore 12/12 |
| T-002 | Python docengine: pdf/images/docx/upscale | done | smoke_docengine 15/15 |
| T-003 | Domain + Application layers | done | unit tests |
| T-004 | Infrastructure adapters + job store | done | unit + integration tests |
| T-005 | API (endpoints, errors, limits, headers) | done | integration tests |
| T-006 | MVC frontend (17 tools, crop UI) | done (build) | browser check pending (T-008) |
| T-007 | Tests + Files CI workflow | done | PR #4 run 45018031155: 71 passed, 0 skipped |
| T-008 | End-to-end local run in browser | done | 13 non-Word tools produced correct output via the MVC UI; Word tools unavailable locally (R-001) |
| T-009 | Word conversions verified (LibreOffice) | done | CI Files run with /usr/bin/soffice; Word_to_pdf_and_images passed |

## Traceability
| REQ | Tasks | Tests |
|---|---|---|
| REQ-1 | T-002, T-003, T-004 | ApiTests: Images_to_pdf_and_back_to_images, Pdf_to_word_keeps_text, Images_to_word_embeds_pictures, Word_to_pdf_and_images, Pdf_page_range_selects_pages; UseCaseTests routing |
| REQ-2 | T-001, T-005 | ApiTests: Compress_resize_and_crop_ratio, Interactive_crop_then_enhance_and_degrade, Target_file_size_is_met, Pad_to_ratio_and_rotate; cargo tests |
| REQ-3 | T-001, T-002 | ApiTests: Interactive_crop_then_enhance_and_degrade, Ai_upscale_doubles_resolution |
| REQ-4 | all | project structure |
| REQ-5 | T-003..T-005 | DomainTests, InfrastructureTests (zip bomb, content sniffing, id/expiry), ApiTests (headers, 404s, validation) |
