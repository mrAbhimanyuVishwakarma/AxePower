# Fuzz regression fixtures

Each folder is one fuzz target (the first argument to `FuzzHarness.Check`). Every file is a
minimized input that once broke that target. The suite that owns the target replays the whole
folder on every run, so a fix cannot regress unnoticed.

A failing fuzz run writes its minimized input to `%TEMP%\AxePower_FuzzArtifacts\`. When you fix
the bug, copy that file here under a name that says what it does, and add a row below. Inputs
that only reproduce at megabyte sizes (hangs) are stored gzipped as `*.bin.gz`.

| Target / file | What it broke | Fix |
|---|---|---|
| `upload-filename/long-extension-256.bin` | `"a."` followed by 254 characters normalized to a 256-character name, which Windows cannot create, so the upload failed at the final move. | `LanDropPathSanitizer.NormalizeFileName` caps the extension at 32 characters. |
| `mediamtx-sessions/non-object-root.bin` | A JSON root that is not an object (`8`) made `TryGetProperty` throw `InvalidOperationException`. | `MediaMtxServer.ParseWebRtcViewers` checks the root's kind first. |
| `mediamtx-sessions/non-object-item.bin` | An `items` entry that is not an object (`{"items":[8]}`) threw the same way. | Non-object items are skipped. |
| `multipart-long-boundary/boundary-15000-near-miss.bin.gz` | A 15,000-character boundary (allowed by the 16 KB request head) with a body that keeps nearly matching it took over 3 s for 1.2 MB. The delimiter scan costs body length times boundary length, so a 2 GB upload meant hours of CPU. | `StreamingMultipartParser` refuses boundaries over RFC 2046's 70 characters (`InvalidDataException`, answered 400). |

Input format for `multipart-*`: the boundary, a `\n`, then the body.
