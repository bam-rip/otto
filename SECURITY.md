# Security

Otto controls a real PC, so its security matters more than most apps'. This page lists what a review of the whole
codebase found (October 2026, version 1.2.0), what 1.2.1, 1.2.2 and 1.3.0 changed, and the risks that remain by design.

**Reporting a problem:** please open a private advisory on GitHub (Security → Report a vulnerability) rather than a
public issue.

## The main threat: text that talks to the AI

Otto's AI reads web pages, emails, files and whatever is on screen. Any of those can contain text written to
trick it ("ignore your instructions and run this…"), known as *prompt injection*. No AI model resists this
reliably, so since 1.2.1 Otto doesn't rely on the model for the dangerous moves: they're checked in code.

Once a task has read something an outsider could have written (search results, a web page, a file, an email,
a browser or mail window on screen), these ask you first until your next message:

- running any PowerShell command
- saving a lasting note (`remember`) or a routine, since those go into every future chat
- running a program or script file
- opening a web address stuffed with data (a way to smuggle information out)

## Found and fixed

| # | Severity | Problem | Fix |
|---|---|---|---|
| 1 | Critical | **Updates weren't signed.** Otto installed whatever zip was attached to the latest GitHub release. Anyone who got into the GitHub account, or a token with release rights, could push code to every install. | Releases are signed with an ECDSA P-256 key kept off GitHub; Otto checks the signature against a public key built into it and refuses anything else. Unsigned releases aren't even offered. |
| 2 | High | **Prompt injection could run commands.** PowerShell only asked first for commands on a short list; anything else ran silently, whoever suggested it. | After untrusted content, every command asks (see above). |
| 3 | High | **Planted instructions could persist.** `remember` and `save_routine` wrote straight into the system prompt of every future chat with no confirmation. | Both ask after untrusted content. Otto's data folder can't be written by `write_file` without asking either. |
| 4 | High | **The "risky command" list was easy to dodge**: `$c='Remove-Item'; & $c`, `& ('Re'+'move-Item')`, scheduled tasks, the Run key, `$PROFILE`, uploads with `Invoke-RestMethod -Method Post`, `certutil`/`mshta`/`rundll32`, `[scriptblock]::Create`. | All of those now ask. The list is still a list (it can't catch everything); #2 is the real protection. |
| 5 | High | **"Ask before paying" was only the AI's judgement.** The screen tool asked only if the model chose to set its own `confirm` field. | Clicking, double-clicking, or pressing Enter/Space on a control labelled like *Buy now, Place order, Checkout, Pay, Subscribe, Sign, Delete account, Empty Recycle Bin* asks, whatever the model said. Checked on the control actually under the pointer. |
| 6 | High | **Windows sign-in hash leak.** `open`, `read_file`, `list_dir`, `write_file` and PowerShell accepted network paths like `\\attacker\share\x`; merely checking one exists makes Windows send your NTLM hash to that server. | File tools refuse network paths; PowerShell commands containing one ask. |
| 7 | High | **Dangerous link types.** `open` passed any `scheme:` link to Windows, including ones used in real attacks (`search-ms:`, `ms-msdt:` "Follina", `ms-officecmd:`, `ms-appinstaller:`). | Only ordinary link types (web, mail, Settings, common apps) open directly; others ask. |
| 8 | Medium | **`write_file` could plant autostart files**: the Startup folder, PowerShell profiles, `.bat`/`.ps1`/`.lnk` files, Program Files, Otto's own folders. | Writing any runnable file type or into those places asks. |
| 9 | Medium | **`fetch_page` could reach this PC and the local network** (router admin pages, local dev servers, `169.254.169.254`), including through redirects and DNS tricks. | It only connects to public addresses, checked at connect time, so redirects and rebinding are caught. Only http/https. |
| 10 | Medium | **Typing into password boxes** was forbidden only by the AI's instructions. | Typing into a field Windows marks as a password box is refused. |
| 11 | Medium | **Program file types missing from the "downloaded program" check**: `.hta .wsf .com .pif .cpl .msc .jar .chm .iso .img .vhd .appinstaller` and more. ISO/IMG files are a common way to dodge Windows' "came from the internet" mark. | The list now covers them, and running any of them after untrusted content asks. |
| 12 | Medium | **IMAP could fall back to plain text.** On ports other than 993 Otto used STARTTLS only if offered, so a network attacker could strip it and read the app password. | TLS is required. |
| 13 | Medium | **Debug switches in release builds.** `--api-test` ran a full AI task with no window and no one watching; `--update-test` installed updates; `--mail-test` wrote your inbox to a temp file. Any program could start Otto with them. | Release builds ignore them unless `OTTO_DEV=1` is set. |
| 14 | Medium | **API keys over plain http.** A custom server address like `http://203.0.113.5/v1` sent your key unencrypted. | Refused unless the server is on this PC. |
| 15 | Low | Runaway PowerShell output was read entirely into memory before trimming. | Capped at 200k characters. |
| 16 | Low | A crafted `.docx` (huge or zip-bomb `document.xml`) could exhaust memory; a missing part crashed the read. | Size-checked, with a clear error. |
| 17 | Low | Update downloads had no size limit. | Capped. |

### Second pass (also in 1.2.2): accidents and less obvious routes

| # | Severity | Problem | Fix |
|---|---|---|---|
| 18 | High | **Typing into a terminal skipped every command check.** The screen tool could open Command Prompt, PowerShell or the Win+R box, type a command and press Enter, and none of the PowerShell checks applied. | Typing into a console or the Run box gets the same checks as `run_powershell`: risky commands always ask, and anything asks after untrusted content. |
| 19 | High | **Summaries could launder planted text.** Long chats get summarised by the AI and the summary is put back as a message from you, built partly from web pages and emails. A planted "the user wants X" could come out as something you asked for. | Outside content is labelled as such in what gets summarised, the summariser is told never to present it as a request, and the summary itself says to follow only your own messages. |
| 20 | Medium | **Undo could lose its own backups.** Only the 200 newest backups were kept, so a task that overwrote more than 200 files deleted the backups of its first changes. | Backups from the last week are never pruned. |
| 21 | Medium | **Backups could fill the disk.** Overwriting a huge file copied it whole first. | Files over 200 MB aren't copied; overwriting one asks, saying it can't be undone. |
| 22 | Medium | **Everyday commands that lose work ran without asking**: `Set-Content`/`Out-File` overwriting a file (no backup, unlike `write_file`), `robocopy /MIR`, `git reset --hard`/`git clean`, `winget uninstall`, force-killing apps (unsaved work), turning off network adapters, `netsh`, `powercfg`. | All ask. |
| 23 | Medium | **No way to see what Otto remembers.** A note planted before 1.2.1 would sit in every chat unnoticed. | Settings → *What Otto remembers* opens the notes and routines to check or edit. |
| 24 | Medium | **The release signing key was stored unprotected.** | It can be locked with a passphrase (`protect-key.ps1`); `publish.ps1` asks for it when signing. |

### Third pass (1.3.0): the features added since

| # | Severity | Problem | Fix |
|---|---|---|---|
| 25 | High | **Blocked places could be reached through PowerShell.** `open`, `fetch_page` and screen control checked the "never touch" list, but a command like `Invoke-WebRequest commbank.com.au` didn't. | Commands naming a blocked site or app are refused too. |
| 26 | Medium | **Ctrl+Alt+A could stop a running program.** With nothing selected it presses Ctrl+C to copy, which in a terminal interrupts whatever is running. | Never pressed in a console; Otto takes a picture of the window instead. |
| 27 | Low | **A scheduled task was dropped silently** when no AI was set up at the time it came due. | You get a notification saying it was skipped and why. |

How the newer features stay safe:
- **Scheduled tasks** run without screen control and can't save notes, routines or more schedules; anything that needs your OK is declined and reported. Creating a schedule asks first after Otto has read untrusted content, so a web page can't plant a recurring job.
- **Places Otto must never touch** are checked against the window title, the program, the browser's address bar, and any address or command Otto is about to use. The list lives in Otto's data folder, which Otto's own file tools can't change without asking.
- **Ctrl+Alt+A** reads the selection through accessibility when the app allows; otherwise it copies and then puts your clipboard back. It takes nothing from blocked places.
- **The spending limit** is enforced in code before each request and between steps, including for scheduled tasks.

## Known risks that remain

These are understood and accepted for now, with what would reduce them:

1. **Screen text in other apps isn't treated as untrusted.** Browsers and mail apps are; a chat app, PDF viewer or game showing planted text isn't. Treating every window as untrusted would make Otto ask before almost everything.
2. **The untrusted state lasts one message.** Text read earlier in a chat is still in the AI's history after you reply. Starting a new chat (+) clears it.
3. **Small leaks are still possible.** After reading a secret, the AI could put a few words of it into a short web address or a search. Only long addresses ask.
4. **Same-user malware wins.** Anything already running as you can read Credential Manager, decrypt Otto's chats (encrypted with your Windows account since 1.2.4, which only stops other accounts and offline disk access), and replace `Otto.exe` (installed per-user, without admin). That's how per-user Windows apps work; encrypting chats with DPAPI would only help against other accounts and offline disk access.
5. **Otto.exe isn't code-signed** (that needs a paid certificate), so Windows SmartScreen warns on first run, and the very first download can't be verified automatically. Updates after that are verified (#1).
6. **What Otto sees goes to your AI provider**: screen text, screenshots, files and emails it reads. Use a local model if that matters.
7. **Checks that read labels can be fooled or miss things.** The Buy/Delete backstop matches English labels; an icon-only or foreign-language button, or one whose label Windows can't read in time, isn't caught. The password-box check relies on the app marking the field as a password.
8. **The PowerShell filter is a list.** It catches common dangerous commands, not every possible one. A command it misses, typed while nothing untrusted has been read, runs without asking.
9. **"Never touch" entries are matched as text.** A short entry like "pay" also blocks anything containing it (PayPal, "payment" in a title); a site reached by a name that doesn't contain the entry (a link shortener's redirect) is caught only once its address or title shows the entry.
10. **The spending limit counts Claude only**, since other providers don't report prices. Set a limit in their own consoles too.
11. **The release signing key** lives on the maintainer's PC. If it's lost, future releases need a new key shipped in a normal (signed) update; if it's stolen, the protection in #1 is gone.
