# `.osa` container format — version 1.1

This document specifies the .osa archive format: what a writer must produce and what a reader must check.

Conventions: fixed-width integers are **little-endian**; `varint` = unsigned LEB128 (readers reject > 10 bytes or
overflow); `svarint` = zig-zag + LEB128; `bytes` = varint length + raw; `str` = `bytes` holding UTF-8.

```
[Header] [Frame 0] [Frame 1] … [Frame n-1] [Index frame] [Footer 32 B]
[Header] [Frame 0] [Frame 1] … [Frame n-1] [Spare index frame] [Header copy] [Index frame] [Footer 32 B]   (§4.1)
```
Block frames are written in the order of the plan, whatever order parallel compression finishes them in, so the
layout says nothing about timing; a block's **frame number** is its position in the file. Segments in the index refer to frame numbers. An archive may carry spare copies of the index
frame and the header (second line, §4.1); readers never depend on them being there.

## 1. Header

| Offset | Size | Field |
|---|---|---|
| 0 | 8 | magic `4F 53 4D 41 1A 0D 0A 00` (`"OSMA" ^Z CR LF NUL`; catches text-mode mangling) |
| 8 | 2 | major = 1 (readers refuse other majors) |
| 10 | 2 | minor = 1 (0 in archives of Osmium 0.1.x). Minor versions only add what older readers can ignore: readers accept every minor of major 1 |
| 12 | 4 | flags. Bits 0–15 are **critical** (refuse if an unknown one is set), bits 16–31 informational. bit0 = encrypted · bit3 = block frames are padded ("Hide exact sizes", §5 item 6; only in archives encrypted with Argon2id, kdf 2, and damage anywhere else) · bit16 = experimental features used |
| 16 | 16 | archive id (random) |
| 32 | 4 | `extLen` |
| 36 | extLen | extension records: `varint type, bytes value`. **Odd type = critical** (refuse if unknown), even = ignorable |
| 36+extLen | 4 | CRC-32 of all preceding header bytes |

Extension records:
- **1 EncryptionInfo** (critical, at most one). The value starts with `u8 kdf`; the rest depends on it:
  - kdf 1 = PBKDF2-HMAC-SHA512: `varint iterations · u8[32] salt · u8 aead · u8[16] verifier`
  - kdf 2 = Argon2id, version 0x13 (RFC 9106): `varint timeCost · varint memoryKiB · varint lanes · u8[32] salt ·
    u8 aead · u8[16] verifier`, with nothing after the verifier

  aead 1 = AES-256-GCM. Another kdf or aead id means the archive needs a newer version of Osmium (or was damaged or
  changed: the header is read before anything can be authenticated, §2.1). A known kdf or aead record is never
  extended: new parameters always come with a new id. A header with two EncryptionInfo records is damaged. §5 says
  how the record is used and which values a reader accepts.
- **2 Creator** (ignorable): `str`, e.g. `Osmium 0.1.0`. Written in unencrypted archives only: an encrypted archive
  keeps its creator in the encrypted index (`creator`, §3), so that the release that made it is not readable without
  the password. Readers show the index's `creator`, else this record (archives of Osmium 0.1.x and encrypted archives
  made before 0.2.0 have only this record).
- Types 3 and 5 (critical) and 4 and 6 (ignorable) are reserved (§9).

## 2. Frame

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `"OBLK"` |
| 4 | 4 | frame flags: bit0 encrypted (must equal header bit0) · bit1 index frame · bit2 reserved (§9) · others must be 0 |
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
Encrypted archives store `AES-256-GCM(body) ‖ tag[16]` instead, under the key `Kenc` (§5), with:
- nonce (12 bytes) = `u64 frameNumber ‖ u32 0`;
- AAD = `archiveId[16] ‖ u64 frameNumber` for a block frame, and `archiveId[16] ‖ u64 frameNumber ‖ header` for the
  index frame (frame number 2^64−1), where `header` is the header's bytes exactly as stored, from the magic to its
  CRC (§1). The index frame therefore authenticates the header: its flags, its key-derivation settings and every
  other record.

The reader takes the frame number from the **index**, not the frame header, so swapped or replayed frames fail
authentication. Descriptor, hashes and payload are all inside the ciphertext: no plaintext hash of content is ever
stored.

Frame headers are not authenticated. In an encrypted archive, a frame header whose CRC checks out but that disagrees
with the index (its number, its length, a missing encrypted flag, the index flag on a block) or that sets a flag this
version does not know was written that way, and the archive is refused as one whose contents are not protected as
Osmium protects them. A frame that fails authentication is damaged or changed (AES-GCM cannot tell which). Neither is
ever reported as made by a newer version: in an encrypted archive only the header (§1) and what the authenticated index
declares (§2.2, §3) can say that. The header is read before anything can be authenticated and anyone can recompute its
CRC, so a reader that meets a critical flag, a critical record type, a key-derivation method or a cipher it does not
know in the header says that the archive needs a newer version **or was damaged or changed**, and refuses it whole. A later version that adds a frame flag to encrypted archives therefore also sets a
critical header flag (§1), so that older readers refuse the archive as newer before they read a frame. A block whose own
descriptor differs from the index's copy is damage in every archive, even when its own copy names a method this version
does not know.

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
the block's pipeline as "needs a newer Osmium (method id 63)", every other block extracts, and reading the block
reports "This archive needs a newer version of Osmium: block 3 uses method id 63." Stored values are checked, never
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
| 8 | codec | `cm2` | `mem` (16–24), `models` (adds bit 8192, x86/x64 instruction contexts), `limit`, `lr`, `lr2`, `minlen`, `stride`, `apm` — needs Osmium 0.2.0 |
| 64 | filter | `x86` | – |
| 65 | filter | `arm64` | – |
| 66 | filter | `delta` | `stride` |
| 67 | filter | `transpose` | `stride` |
| 68 | filter | `pcm16` | `offset`, `length`, `channels`, `lms`, `split` |
| 69 | filter | `image` | `offset`, `width`, `rows`, `bpp`, `rowbytes`, `green` |
| 70 | filter | `text` | – (its dictionary travels inside the filtered stream, so it gets compressed too) |
| 71 | filter | `x86v2` | `calls` (0/1): x64 RIP-relative operands, and with `calls=1` CALL/JMP rel32, as block positions — needs Osmium 0.2.0 |

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
varint metaCount · (str key · bytes value)*        e.g. "creator" (UTF-8, what made the archive; in every archive
                                                   since 0.2.0, the writer's own value), "profile", "plan" (JSON explain
                                                   log), "rules" (JSON), "spares" (u64 offset of the spare index frame, §4.1)
```
- Length-prefixed records let a newer minor version append fields that older readers skip.
- Frame offsets are implicit: `offset(frame k) = headerLength + Σ_{j<k} (gapBefore_j + frameLength_j) + gapBefore_k`.
- A file is the concatenation of its segments; segments of different files may overlap (dedup). Empty files have no
  segments; every directory (including empty ones) is stored so attributes and times can be restored.
- Reader invariants: no duplicate paths; Σ segment lengths = size; offset + length ≤ rawSize of the frame; rawSize ≤ 2^30;
  path ≤ 32 KiB; stageCount ≤ 8; decodeMemKiB ≤ the configured limit (otherwise ask before decoding).
- Readers check the index as they decode it, before they allocate anything for what it declares: every count against
  the bytes that are left (at the fewest bytes an item can take), the entry count and every path's depth against their
  own limits, and the path text the front coding builds up against a cap (64 bytes per byte of index, at least 64 MiB
  and at most 1 GiB), as a path may repeat most of the one before it in a few bytes.
- Unknown metadata keys are ignored.

## 4. Footer (last 32 bytes)

| Offset | Size | Field |
|---|---|---|
| 0 | 8 | index frame offset |
| 8 | 8 | index frame length |
| 16 | 8 | archive id bytes 0..7 |
| 24 | 4 | CRC-32 of bytes 0..23 |
| 28 | 4 | magic `"OSAE"` |

The index frame ends where the footer starts: `indexOffset + indexLength + 32 = file length`. Readers check this and
that every block ends at or before `indexOffset`; nothing reads the bytes between the last block and the index frame
unless the index points there (§4.1).

### 4.1 Spare copies (container 1.1)

An archive of container 1.1 may carry spare copies of its index frame and its header. Whether it does is the writer's
choice; readers never depend on it. Osmium's writer adds them by default where they cost little: when its block frames
(header and body) total at least 1 MiB (2^20 bytes) and the index frame is at most 2 % of those bytes. Archives of many
small files, whose file list is large next to their data, therefore get none unless the writer is asked for them; the
writer can also be told to add them whenever an archive has a block, or never.

```
… [last block frame] [B: copy of the index frame] [H: header copy] [A: index frame] [footer 32 B]
```

- **A** is the index frame as in §3; the footer points to it. Its metadata holds the key `spares` with B's offset (u64
  little-endian). B is written first, so the writer takes B's offset (the end of the last block), serializes the
  index with that key, compresses and (in an encrypted archive) seals it **once**, and writes the same bytes as B and
  as A.
- **B** is a byte-identical copy of A's whole frame, header and body: frame number 2^64−1 and the index flag, like A.
- **H** is the header bytes exactly as at offset 0, then zero padding, then a 32-byte trailer. The padding makes H at
  least 4096 bytes long, so that B ends at least 4 KiB before A starts and one burst of damage does not take both.

  | Offset | Size | Field |
  |---|---|---|
  | 0 | 4 | magic `"OHDR"` |
  | 4 | 4 | flags = 0 |
  | 8 | 8 | header length |
  | 16 | 8 | padding length |
  | 24 | 4 | reserved = 0 |
  | 28 | 4 | CRC-32 of bytes 0..27 |

  So `spares + indexLength + headerLength + paddingLength + 32 = indexOffset`. A writer uses the least padding that
  makes H 4096 bytes long (none when the header is longer) and may add more, for example to align A, but the padding
  length is always below 65,536: readers take a trailer with more as damaged.

The spare copies cost a copy of the index frame plus at least 4 KiB. The index frame's length, which decides whether they
are worth it, is known before the index is sealed (the tag adds 16 bytes), so the index is sealed once either way. An
archive without spare copies has no `spares` key, and still has minor 1.

Readers:
1. **Every open** with an intact A that holds `spares` checks H's trailer (magic, CRC, flags and reserved 0, lengths a
   header can have) and that the offsets add up as above, with B after the last block. If not, or if the trailer cannot
   be read, the archive opens with the warning "The spare copy of the file list is damaged; the archive is complete. Copy it to a healthy disk." A
   trailer whose CRC checks out but which has a flag or reserved bit set was written by a newer version: the reader
   does not use H and gives no warning.
2. **Test** compares B with A byte for byte and H's header bytes with the header at offset 0, and checks that the
   padding is zero; a difference is a warning, the archive is complete. This is not done on every open: it reads the
   index twice. **Salvage** counts B as the spare copy when it verifies and has A's content, and H as the header's copy
   when its header bytes are those of the header at offset 0 (with that header damaged: when they carry the archive id
   that the footer at the end of the file names) and its padding is zero; anything else is reported as damage, though
   no data is lost.
3. **A fails** (frame CRC, GCM tag, payload hash, decompression, raw hash or index structure, or its bytes cannot be read
   at all, as with a bad sector): the reader reads B, found through H's trailer or, when that is damaged too, as the
   frame header with the index flag, the index frame number and the footer's index length that is nearest before A
   within `indexLength + 1 MiB + 72 KiB` of `indexOffset` (sectors that cannot be read are skipped). B gets
   every check A gets, and its index must name B's own offset in `spares`. The archive then opens with the warning "The
   file list of this archive was damaged; Osmium used its spare copy. Every file can be checked with Test." Test
   reports it too. A wrong password, a missing password and a feature of a newer version are not damage and never lead
   to B. When B fails as well, the error is A's.
4. **The header fails** for any reason (magic, major version, CRC, lengths, or a read error) while the footer checks out, places the index
   frame at the end of the file, and H's trailer checks out: the reader takes the header copy from H and checks it the
   same way; its archive id must match the footer's. An encrypted archive's index is then authenticated with H's
   header bytes, which are the original header bytes. Warning: "The header of this archive was damaged; Osmium used its
   spare copy." An intact header with a feature of a newer version is never replaced by its copy.
5. **The footer fails**: the archive does not open, and the error says "The end of this archive is damaged. Try Recover
   files in the app or osmium salvage." Salvage scans for frames and finds A or B (§8).

A reader of container 1.0 checks the header without looking at the minor version, follows the footer to A and ignores
metadata keys it does not know; it never reads B or H. Archives with spare copies therefore open in Osmium 0.1.x as
any other archive does (§10).

## 5. Encryption
Encryption uses only standard, published algorithms. An encrypted archive is meant to stay safe with every detail of
this format known, except the password.
1. **Password.** The password in Unicode normalisation form C, encoded as UTF-8, for every kdf. Osmium refuses empty
   passwords and passwords that are not valid text (an unpaired surrogate) when it creates an archive; a reader treats
   them as wrong without deriving.
2. **Key derivation**, by the header's kdf id (§1). kdf 2 (the default since 0.2.0): `master = Argon2id(P, S, t, m, p,
   T = 32)` (RFC 9106, version 0x13, type 2) with the password bytes P, the 32-byte salt S and the header's time cost
   t, memory m (KiB) and lanes p; the secret K and the associated data X are empty. Osmium's presets are Standard
   (1 GiB, 2 passes, 4 lanes), Strong (2 GiB, 3 passes, 4 lanes) and Maximum (4 GiB, 3 passes, 4 lanes). kdf 1
   (Osmium 0.1, and the Compatible protection, 600 000 iterations by default): `master = PBKDF2-HMAC-SHA512(P, salt,
   iterations, 32 bytes)`.
3. **Keys.** `Kenc = HKDF-Expand(SHA-256, master, 32, "osmium/v1/aead")`; `verifier = HKDF-Expand(SHA-256, master, 16,
   "osmium/v1/verify")`, compared in constant time, so a wrong password fails after one derivation and before any
   data is read. The verifier lets a guess be checked as fast as decrypting the file list would; each guess costs one
   key derivation.
4. **Frames.** AES-256-GCM with `Kenc`, nonce `u64 frameNumber ‖ u32 0`, AAD `archiveId ‖ u64 frameNumber`; the index
   frame's AAD also holds the exact header bytes, so changing the kdf id, its parameters, the salt or the flags makes
   the index fail authentication (§2.1).
5. **Uniqueness.** The salt is 32 random bytes per archive, so `Kenc` is unique per archive. A frame number is sealed
   at most once per key: the `(frameNumber, 0)` nonces never repeat, and a writer that failed stops for good instead
   of sealing again. The spare copy of the index (§4.1) is a byte copy of sealed bytes, not a second encryption; any
   future re-encrypted copy gets its own frame number.
6. **Padding.** In kdf 2 archives the plaintext of the index frame ends in zero bytes up to the Padmé length (`L`
   rounded up so that its low `E − S` bits are zero, `E = ⌊log2 L⌋`, `S = ⌊log2 E⌋ + 1`); with header flag bit 3,
   every block frame is padded the same way ("Hide exact sizes"). `payloadLen` stays exact; a reader checks that the
   padding is zero, and allows it in block frames only with bit 3. kdf 1 archives are never padded.
7. **Limits** a reader checks before it derives: Argon2id `1 ≤ t ≤ 16`, `1 ≤ p ≤ 64`, `8·p ≤ m ≤ 8 388 608` (8 GiB),
   `t·m ≤ 33 554 432`; PBKDF2 `1 ≤ iterations ≤ 20 000 000`. Outside them the archive is refused as damaged. Within
   them a reader asks before it uses more memory than its configured memory limit, as for `decodeMemKiB`, and before
   it does more work than the strongest preset (`t·m > 12 582 912`). All of these checks come before the password is
   asked for. In a kdf 2 archive the index frame, which a reader reads whole before it can be authenticated, is at
   most 256 MiB, and a reader refuses a larger one before it reads it. Every other archive (unencrypted, kdf 1, and
   every archive of Osmium 0.1.x) keeps the bound of any frame: a body of at most 2 GiB, which a reader also checks
   before it knows the header.
8. **Minimum protection.** Osmium writes at least Argon2id with m = 256 MiB and t·m = 768 MiB-passes, or PBKDF2 with
   100 000 iterations. Below that a reader asks before it opens an archive (the command line needs
   `--allow-weak-protection`): such an archive comes from Osmium 0.1's command line with a low `--kdf-iterations`, or
   from modified software. The minimum never rises.
9. **Encrypted means encrypted.** In an archive whose header says encrypted, every frame must be encrypted and
   authenticate, and every frame header must match the index (its number, its length and its flags, with no flag the
   reader does not know); a reader refuses anything else before it lists or writes anything, and no setting lifts
   that. When every block it reads fails authentication, the archive is refused the same way; when only some do, those
   blocks are damage. Every other byte is header, frame header, an exact copy of one of them, zero padding, the
   header-copy trailer or the footer. A reader refuses, unless the user agrees to open the archive anyway (the extra
   bytes are then ignored and a warning says so; of a header and a header copy that differ, the one the index
   authenticates is used): unknown ignorable header records; more than one Creator record, or one longer than 64
   bytes or with control characters; bytes after the PBKDF2 verifier; `gapBefore` other than 0; blocks that do not end
   where the spare copy B or the index frame begins; a B that differs from the index frame; a header copy that differs
   from the header; non-zero header-copy padding; and a header-copy trailer of a newer version.
10. **Extending encrypted archives.** New information goes inside the encrypted index, or is signalled by a critical
   flag or a critical (odd-type) record, so that older readers say "needs a newer version". Plaintext ignorable
   records, unreferenced frames and newer header-copy trailers are never added to encrypted archives; the reserved
   ignorable types and frame magics of §9 are for unencrypted archives.
11. **What stays visible**: that this is an Osmium archive, the container version, the archive id, the flags, the
   key-derivation settings and salt, the frame headers (the number of blocks and their sizes, rounded when sizes are
   hidden), the length of the index frame (padded in kdf 2 archives), and the footer. File names, sizes, times,
   methods, the creator and contents are encrypted. Compressed sizes say something about the content: they let
   someone who has a candidate file check whether the archive probably holds it, and when files an attacker chooses
   share a solid block with secret files, the compressed size can reveal parts of the secret (compression oracle).
   "Hide exact sizes" rounds the sizes; it does not remove the effect. Keep files that others choose out of encrypted
   archives that hold secrets when sizes matter.
12. **What a reader cannot check**: whether a writer leaked the password through fields that must look random (salt,
   archive id). Use genuine builds; their provenance can be checked (`gh attestation verify <file> --repo
   windfastt/osmium`) and, once releases are signed, their code signature.

## 6. Integrity checks
1. Header CRC, footer CRC + magic + archive id match.
2. Frame header CRC; body CRC (plain) or GCM tag (encrypted); payloadHash.
3. After decoding: every stage size, then rawHash (XxHash128).
4. Per file: XxHash128 of the reconstructed content.
5. With spare copies: H's trailer on every open; B against A and H against the header in Test and salvage (§4.1).

## 7. Extraction safety
Rejected paths: absolute, drive letters, UNC, empty / `.` / `..` components, `\`, `:` (ADS), NUL and control
characters, Windows reserved device names (`CON PRN AUX NUL COM1-9 LPT1-9`, with or without extension), and anything
whose resolved full path escapes the destination.

Every target is planned before anything is written:
- Every entry's own resolved name is reserved first. Names compare as the file system does: case-insensitively on
  Windows and macOS, exactly elsewhere.
- In index order (parents before children), a name that another entry of the same extraction already took, or a file
  where another entry needs a folder (or the other way round), gets a free name — `name (2).ext`, `name (3).ext`, … — in
  every overwrite mode: an entry never replaces another entry of the same archive. A free name exists nowhere in the
  destination, is no entry's own name and was not chosen before.
- A name that exists in the destination follows the overwrite mode: skip, fail, a free name, or replace. Only a file
  planned to be replaced is replaced, never a folder, and never a file the system finds only under another spelling
  (such as a short 8.3 name).
- Nothing is created or written below a folder that already exists in the destination and is a symbolic link, a
  junction or another reparse point that stands for another name; such entries are refused and counted as unsafe.
  Other reparse points (cloud placeholders) are ordinary folders. The destination itself may be a link.

Each file is written to a new part file, `name.osmium-part-<random>`, and verified against its hash; then the
Mark-of-the-Web of a downloaded archive goes on the part file, the part file is renamed to the file's name, and then
the file's times and attributes are set. Folder times and attributes are set at the end, deepest first, only on
folders the extraction created (never System or ReadOnly, which would make Windows act on a desktop.ini); a folder
that was already there keeps its own. Memory is reserved for the largest stage of each block's pipeline. A cancelled or
failed extraction deletes its own part files and the folders it created that are still empty; finished files stay.

## 8. Salvage
`osmium salvage <archive> <dest>` reads a damaged archive through its frames, never through the footer's or the
index's offsets:
1. It scans the whole file, from offset 0, for frame markers whose header CRC checks out. A frame whose body verifies
   (body CRC and payloadHash, or the GCM tag) is skipped whole, so the frames of an archive stored in an intact block
   are never seen. After a frame that does not verify, the scan resumes one byte later, so frames after a missing or
   shortened region are still found; a frame found inside the bytes a damaged frame claimed is marked as such.
2. A block counts only once decoded: every stage size, then rawHash. A pipeline, frame flag or index of a newer version
   is reported as needing a newer Osmium, never as damage, except a frame flag in an encrypted archive: frame headers
   are not authenticated, so there it is damage (§2.1).
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
6. Encryption: the key-derivation settings, the salt and the archive id exist only in the header and, in archives with
   spare copies, in the header copy H (§4.1), which opening uses when the header fails (the footer keeps the first 8
   bytes of the id). Salvage reads them from the header at offset 0. When that is damaged, it looks for an intact H
   beside each index frame (frame headers are not encrypted, so index frames are found without the key) whose archive
   id matches the footer at the end of the file when there is one, and uses it once its password verifier accepts the
   password and frames authenticate under it; when that footer vouches for H and the verifier refuses the password,
   the password is wrong. Otherwise it reads them from the damaged header. Block frames are authenticated with
   `archiveId ‖ frameNumber` alone, so when the header is damaged elsewhere its key fields, read from their fixed
   places (the record at offset 36; for kdf 2 the three varints after the kdf byte, then salt, cipher id and
   verifier), still yield the blocks once they authenticate a frame. When the kdf byte itself is damaged, kdf 2 is
   tried first, then kdf 1; a reading that would be refused (too weak, too much memory or work) counts as one that
   does not authenticate. The limits of §5 apply before every derivation, and every check of the settings comes
   before the password is asked for. The index frame also binds the exact header bytes: salvage rebuilds the header
   from those fields as a writer makes it (with the minor version stored in it, 1, or 0 for archives of Osmium 0.1.x;
   without a Creator record first, as encrypted headers are written since 0.2.0, then with the creator text found in
   the damaged header; with every flag set a writer of this version uses), and a rebuild that authenticates the index
   restores the file names. Without the key fields, encrypted frames cannot be decrypted.

   Salvage never writes a frame that is not encrypted from an archive that counts as encrypted, and the report says
   why it counts as encrypted and what to do. An intact header decides. Without one, the archive counts as encrypted
   when an intact header copy, the index frame the footer at the end points to, the damaged header, or a password
   given before salvage started says so, or when an encrypted frame is found: a frame that cannot be checked without
   the key is never ignored. Only an encrypted frame inside the bytes of a block that a plaintext index frame at the
   end lists does not count (an unencrypted archive whose damage uncovers an encrypted archive stored in one of its
   blocks is still salvaged), so a plaintext file list put beside an encrypted archive's frames cannot make salvage
   write what it lists. Salvage never writes a frame whose padding is not zero.
7. Nothing is written when what would be written exceeds the destination's free space, or expands more than the limit
   (`--force` lifts both in the CLI).
8. Spare copies (§4.1): the header copy H is recognised by its trailer and the header it holds, which must be this
   archive's (§4.1, readers, item 2), so it is not reported
   as unrecognised data, and the spare index frame B, which the index names in `spares`, is a copy of the index frame,
   not damage: an intact archive with spare copies salvages as complete. When A is destroyed, B is the index salvage
   uses, and every file comes back with its name.

## 9. Reserved identifiers
So that later versions can add them without clashing, these are reserved; this version never writes them:
- Frame flag bit 2: an explicit nonce, for archives that can be appended to later. A reader that does not know it says
  an unencrypted archive needs a newer version (§2.2); in an encrypted archive it comes with a critical header flag,
  since a frame flag alone is refused there as a change (§2.1).
- Header extension types 4 and 6 (ignorable; in unencrypted archives only, §5 item 10) and 3 and 5 (critical, §1).
- Frame magics `ORDR` and `ORPR` (recovery records) and `OCHK` (checkpoints), in unencrypted archives only (§5 item
  10). Readers skip bytes that no index references, so such frames never disturb an older reader.

## 10. Compatibility

| Archive | Osmium 0.1.x | Osmium 0.2.0 and later |
|---|---|---|
| 1.0 (written by 0.1.x) | opens, tests, extracts | opens, tests, extracts; no spare copies |
| 1.1 whose blocks use only methods 0.1.x knows, with or without spare copies | opens, tests, extracts: the spare copies and the minor version never stop it | opens, tests, extracts; uses the spare copies when the index frame or header is damaged |
| 1.1 with a block made with a method new in 0.2.0 (such as `cm2` or `x86v2`, §2.2) | refuses the whole archive while reading the index, saying it was made by a newer version; nothing is extracted | opens it; only blocks that a reader does not know fail, and the other files extract |
| Encrypted with Argon2id (kdf 2, the default protection since 0.2.0) | refuses it before asking for the password: "Unsupported key-derivation function."; nothing is extracted | opens, tests, extracts with the password |
| Encrypted with Argon2id and "Hide exact sizes" (header flags `0x0009`) | refuses it before asking for the password: "Archive uses features this version does not understand."; nothing is extracted | opens, tests, extracts with the password |
| Encrypted with PBKDF2 (kdf 1: archives of 0.1.x, and the Compatible protection since 0.2.0) | opens, tests, extracts with the password (a 0.2.0 archive has no Creator record; 0.1.x shows no creator) | opens, tests, extracts with the password; one below 100 000 iterations only after the user agrees (§5 item 8) |

The index carries a copy of every block's pipeline, so 0.1.x meets a method it does not know while reading the index;
readers from 0.2.0 on give such a block a stand-in and fail only that block (§2.2).
