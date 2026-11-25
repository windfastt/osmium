# `.osa` container format — version 1.0

Merged design (engine architect proposal + lead synthesis; see IMPROVISATION.md for what changed and why).

Conventions: fixed-width integers are **little-endian**; `varint` = unsigned LEB128 (readers reject > 10 bytes or
overflow); `svarint` = zig-zag + LEB128; `bytes` = varint length + raw; `str` = `bytes` holding UTF-8.

```
[Header] [Frame 0] [Frame 1] … [Frame n-1] [Index frame] [Footer 32 B]
```
Frames are written in completion order (parallel compression finishes out of order); a block's **frame number** is
its position in the file. Segments in the index refer to frame numbers.

## 1. Header

| Offset | Size | Field |
|---|---|---|
| 0 | 8 | magic `4F 53 4D 41 1A 0D 0A 00` (`"OSMA" ^Z CR LF NUL`; catches text-mode mangling) |
| 8 | 2 | major = 1 (readers refuse other majors) |
| 10 | 2 | minor = 0 (additive changes only) |
| 12 | 4 | flags. Bits 0–15 are **critical** (refuse if an unknown one is set), bits 16–31 informational. bit0 = encrypted · bit16 = experimental features used |
| 16 | 16 | archive id (random) |
| 32 | 4 | `extLen` |
| 36 | extLen | extension records: `varint type, bytes value`. **Odd type = critical** (refuse if unknown), even = ignorable |
| 36+extLen | 4 | CRC-32 of all preceding header bytes |

Extension records:
- **1 EncryptionInfo** (critical): `u8 kdf (1 = PBKDF2-HMAC-SHA512) · varint iterations · u8[32] salt · u8 aead (1 = AES-256-GCM) · u8[16] verifier`
- **2 Creator** (ignorable): `str`, e.g. `Osmium 0.1.0`

## 2. Frame

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `"OBLK"` |
| 4 | 4 | frame flags: bit0 encrypted (must equal header bit0) · bit1 index frame · others must be 0 |
| 8 | 8 | frame number (blocks `0..n-1` in file order; the index frame uses `2^64-1`) |
| 16 | 8 | body length |
| 24 | 4 | CRC-32 of bytes 0..23 |
| 28 | body length | body |

### 2.1 Block body (plaintext form)
```
bytes descriptor
u8[16] rawHash        XxHash128 of the raw (pre-filter) block content
u8[8]  payloadHash    XxHash3-64 of payload (quick integrity test without decoding)
varint payloadLen
u32    CRC-32 of all preceding body bytes
u8[payloadLen] payload
```
Encrypted archives store `AES-256-GCM(body) ‖ tag[16]` instead, with nonce = `u64 frameNumber ‖ u32 0` and
AAD = `archiveId ‖ u64 frameNumber`. The reader takes the frame number from the **index**, not the frame header, so
swapped or replayed frames fail authentication. Descriptor, hashes and payload are all inside the ciphertext: no
plaintext hash of content is ever stored.

### 2.2 Pipeline descriptor
```
varint stageCount (1..8)               stages in ENCODE order; the last stage is the codec
stage*: varint methodId
        varint inputSize               bytes that entered this stage when encoding (stage 0 = raw block size)
        bytes  params                  varint count · (str name · svarint value)*, names strictly ascending
```
Decoding runs the stages in reverse; each must produce exactly its recorded `inputSize`, then the final output's
XxHash128 must equal `rawHash`. A method id, parameter name or parameter value outside the declared range that this
reader does not know makes only that block undecodable, never the whole archive: the archive opens, listings show
the block's pipeline as "needs a newer Osmium (method id 8)", every other block extracts, and reading the block
reports "This archive needs a newer version of Osmium: block 3 uses method id 8." Stored values are checked, never
clamped, because a value from a newer writer would decode differently. The same message is used for the other
features a newer writer can add: unknown critical header flags (checked only after the header CRC, so damage is
still reported as damage), critical header extensions, key-derivation functions and ciphers, index versions and
frame flags. An index compressed with an unknown method cannot be opened, and says so.

| Id | Kind | Name | Parameters |
|---|---|---|---|
| 0 | codec | `store` | – |
| 1 | codec | `deflate` | `level` |
| 2 | codec | `brotli` | `quality`, `window` |
| 3 | codec | `zstd` | `level`, `wlog`, `ldm`, `ldmmin`, `strategy` |
| 4 | codec | `lzma` | `dict`, `lc`, `lp`, `pb`, `fb`, `mf` — the 5-byte LZMA properties are rebuilt from these |
| 5 | codec | `bzip2` | – |
| 6 | codec | `ppmd` | `order`, `mem` |
| 7 | codec | `cm` | `mem`, `models`, `limit`, `lr`, `lr2`, `minlen`, `stride`, `apm` |
| 64 | filter | `x86` | – |
| 65 | filter | `arm64` | – |
| 66 | filter | `delta` | `stride` |
| 67 | filter | `transpose` | `stride` |
| 68 | filter | `pcm16` | `offset`, `length`, `channels`, `lms`, `split` |
| 69 | filter | `image` | `offset`, `width`, `rows`, `bpp`, `rowbytes`, `green` |
| 70 | filter | `text` | – (its dictionary travels inside the filtered stream, so it gets compressed too) |

Ids ≥ 128 are reserved for experimental methods.

## 3. Index frame
The index frame is an ordinary frame (flag bit1) whose body is a block body: its descriptor says how the index was
compressed (zstd by default, LZMA on Ultra/Insane) and its rawHash covers the raw index.

Raw index:
```
varint indexVersion (= 1)
varint blockCount
  block* (frame-number order), each wrapped as `bytes` (length-prefixed record):
          varint gapBefore (normally 0) · varint frameLength (incl. 28-byte frame header)
          varint rawSize · varint decodeMemKiB · bytes descriptor (copy of the block's pipeline descriptor, so
          listings can show methods without decrypting/reading every frame)
varint entryCount                     sorted by ordinal byte order of path (parents before children)
  entry*, each wrapped as `bytes` (length-prefixed record):
          varint sharedPrefix · varint suffixLen · u8[suffixLen] suffix      (front-coded path)
          u8 kind (0 file, 1 directory)
          varint present (bit0 mtime · bit1 ctime · bit2 attributes)
          [u64 mtime FILETIME UTC] [u64 ctime FILETIME UTC] [varint attributes & 0x2027]
          file: varint size · varint segmentCount · (varint frame · varint offset · varint length)* · u8[16] XxHash128
varint metaCount · (str key · bytes value)*        e.g. "creator", "profile", "plan" (JSON explain log), "rules" (JSON)
```
- Length-prefixed records let a newer minor version append fields that older readers skip.
- Frame offsets are implicit: `offset(frame k) = headerLength + Σ_{j<k} (gapBefore_j + frameLength_j) + gapBefore_k`.
- A file is the concatenation of its segments; segments of different files may overlap (dedup). Empty files have no
  segments; every directory (including empty ones) is stored so attributes and times can be restored.
- Reader invariants: no duplicate paths; Σ segment lengths = size; offset + length ≤ rawSize of the frame; rawSize ≤ 2^30;
  path ≤ 32 KiB; stageCount ≤ 8; decodeMemKiB ≤ the configured limit (otherwise ask before decoding).

## 4. Footer (last 32 bytes)

| Offset | Size | Field |
|---|---|---|
| 0 | 8 | index frame offset |
| 8 | 8 | index frame length |
| 16 | 8 | archive id bytes 0..7 |
| 24 | 4 | CRC-32 of bytes 0..23 |
| 28 | 4 | magic `"OSAE"` |

## 5. Encryption
- `master = PBKDF2-HMAC-SHA512(UTF-8(NFC(password)), salt[32], iterations, 32 bytes)`; default 600 000 iterations
  (stored, so tunable per archive).
- `Kenc = HKDF-Expand(SHA-256, master, 32, "osmium/v1/aead")`;
  `verifier = HKDF-Expand(SHA-256, master, 16, "osmium/v1/verify")` compared in constant time → instant
  "wrong password" without touching data.
- Salt is random per archive ⇒ `Kenc` is unique per archive ⇒ (frameNumber, 0) nonces never repeat.
- Plaintext metadata that remains: header, frame headers (block count and sizes), footer. File names, methods and
  content are encrypted.

## 6. Integrity checks
1. Header CRC, footer CRC + magic + archive id match.
2. Frame header CRC; body CRC (plain) or GCM tag (encrypted); payloadHash.
3. After decoding: every stage size, then rawHash (XxHash128).
4. Per file: XxHash128 of the reconstructed content.

## 7. Extraction safety
Rejected paths: absolute, drive letters, UNC, empty / `.` / `..` components, `\`, `:` (ADS), NUL and control
characters, Windows reserved device names (`CON PRN AUX NUL COM1-9 LPT1-9`, with or without extension), and anything
whose resolved full path escapes the destination. Files are written to `name.osmium-part`, verified, then renamed;
directory times and attributes are applied deepest-first after their contents; ReadOnly is applied last.

## 8. Salvage
`osmium salvage <archive> <dest>` reads a damaged archive through its frames, never through the footer's or the
index's offsets:
1. It scans the whole file, from offset 0, for frame markers whose header CRC checks out. A frame whose body verifies
   (body CRC and payloadHash, or the GCM tag) is skipped whole, so the frames of an archive stored in an intact block
   are never seen. After a frame that does not verify, the scan resumes one byte later, so frames after a missing or
   shortened region are still found; a frame found inside the bytes a damaged frame claimed is marked as such.
2. A block counts only once decoded: every stage size, then rawHash. A pipeline, frame flag or index of a newer version
   is reported as needing a newer Osmium, never as damage.
3. Which archive is this one: its id comes from the header at offset 0, else from the footer at the end of the file. A
   header found after offset 0 (data prepended to the archive) is used only when that footer names the same archive,
   since an archive stored in a block has an intact header too.
4. The index: an index frame whose footer names another archive is set aside, and the one the footer at the end of the
   file follows wins. Without footers, an index is set aside when it lies inside a damaged frame, within the bytes the
   blocks of another index occupy, or before intact frames it does not list (an archive has one index, after all its
   frames); of the rest, the one that lists the most frames found wins. The frames a set-aside index lists belong to
   another archive. Each block of the chosen index is matched to a found frame by frame number, frame length, raw size
   and descriptor, and a file is written (§7) only when all its blocks verified and its XxHash128 matches.
5. Without an intact index, each verified block is written as `block-<frame number>.bin`, with a `salvage-report.txt`
   giving its pipeline, sizes and hashes; file names, folders and times are lost. A frame that may belong to another
   archive is written as `block-<frame number>-at-<offset>.bin` and never stands for this archive's block. An
   unencrypted frame does not name its archive, so where the frames stop following one another the report says so.
6. Encryption: the salt, iterations and archive id exist only in the header (the footer keeps the first 8 bytes of the
   id). Block frames are authenticated with `archiveId ‖ frameNumber` alone, so when the header is damaged elsewhere
   its key fields, read from their fixed places, still yield the blocks once they authenticate a frame. The index frame
   also binds the exact header bytes: salvage rebuilds the header from those fields as a writer makes it, and a rebuild
   that authenticates the index restores the file names. Without the key fields, encrypted frames cannot be decrypted.
7. Nothing is written when what would be written exceeds the destination's free space, or expands more than the limit
   (`--force` lifts both in the CLI).
