# MT101 print files (.prt) — what the parser relies on

Notes from the real sample `00663459.prt` (not in the repo: real files may contain customer data — keep them in the git-ignored `tests/SwiftBatchProcessor.Tests/Fixtures/real/`).

## File name

`006` + last digit of the year + ascending number: `00663459` = 2026, file #3459.

## Layout

- Text print format, **CRLF** line endings, tabs present, sometimes NUL padding at the end.
- Each message starts with a print header line whose trailing number is the **OSN**:

  ```
  06/07/26-11:01:12<TAB>   MT942PMNTS-3459-190768 <TAB> 1
  ```

  Regex: `^\s*\d{2}/\d{2}/\d{2}-\d{2}:\d{2}:\d{2}\s+[A-Z0-9_]+(?:-\d+)*-(\d+)\b` → `190768`.
  Page breaks can repeat the same header (same OSN) in the middle of a message.
- The message header block carries `Sender : <BIC>`, `Receiver : <BIC>` and sometimes `Currency : XXX`.
- **Tag lines hold the field description; the value is on the following line(s)**, up to the next tag line, print header or separator line (`-----`):

  ```
         20: Sender's Reference
             REF0663459
         50H: Ordering Customer
             /GR7201100000000012345678901
             ACME TRADING S.A.
         21: Transaction Reference
             TXN0000001
         32B: Currency/Transaction Amount
             Currency : EUR
             Amount   :            #12.345,67#
         57A: Account With Institution - BIC
             CHASGB2L
  ```

- Amounts are written as `#1.234,56#` (dot thousands, comma decimals).
- Raw FIN lines (`:20:VALUE`) are also accepted: the inline remainder becomes the first value line.

## Fields used

| Tag | Level | Use |
|---|---|---|
| print header | message | OSN (min/max over the file; single header + N orders → from + N − 1) |
| `Sender :` / `Receiver :` | message | sender BIC (customer validation), receiver |
| 20 | message (seq. A) | Sender's Reference |
| 30 | message, may be overridden per payment | requested execution date (YYMMDD) |
| 50a | message or payment | ordering customer: `/account` line + first name line (`1/NAME` prefixes stripped). 50C/50L = instructing party → ignored |
| **21** | payment (seq. B) | **starts a new payment**; unique ascending transaction reference |
| 32B | payment | currency + amount |
| 57a | payment | creditor bank BIC = first BIC-shaped line (skipping `/account`) |
| 59a | payment | beneficiary `/account` + name; BIC fallback for the creditor bank |

`MTF` iff the creditor BIC (57a, else 59a) is exactly `PIRBGRAA` or `PIRBGRAAXXX`; otherwise `MT103`.

A new payment is also closed by a new `20:`, a new `Sender :`/`Receiver :` header line, or a print header with a **different** OSN — so multi-message files keep each message's 20/30/50 context.

## Sample expectations (00663459.prt)

Single payment · OSN 190768 · 57A `CHASGB2L` → MT103 · 50H ordering IBAN `GR72…`.
