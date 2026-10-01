<p align="center"><img src="assets/otto.png" width="96" alt="Otto icon"></p>

# Otto

**Operates Tasks Through Onscreen controls.** Otto is an AI assistant for Windows that uses your PC the way you do. It can see the screen, click, type, scroll, open apps, sort files, browse the web, change settings, and read and write your email.

It lives in the system tray. Press **Ctrl+Shift+J** and a panel slides in from the right, like the Windows 10 Action Center. Ask for something and Otto does it. While it's working, a soft glow around the screen shows that it has the mouse and keyboard.

> **Read the [safety notes](#safety) before you use it.** Otto can control your computer with very few restrictions. That's the point of it, but it's also the risk.

## What it can do

- **See and control the screen.** It reads windows through Windows' accessibility interface (cheap and accurate), and uses screenshots when it needs to see something visual. It can click, type, drag, scroll and press shortcuts.
- **Work with your apps and files.** It can open any installed app by name, manage windows, read and write files (including `.docx`), and run PowerShell.
- **Use the web.** It searches, reads pages, and works inside your normal browser.
- **Email and calendar** for Outlook, Hotmail and Microsoft 365: list, read, draft and send email (always with your approval), and check or add calendar events.
- **Remember things.** It keeps short notes about your apps and preferences, and saves multi-step jobs as routines that replay later without the AI.
- **Voice.** Push-to-talk dictation (Ctrl+Alt+J). It uses Windows' online speech engine, or your AI provider transcribes the recording (Gemini and OpenAI). What you said goes into the message box for you to check before sending. Otto can also read replies aloud.
- **Message controls.** Hover a message to copy it, retry Otto's last answer, or edit and resend your last message. Right-click for more.
- **Pick your AI.** It works with Anthropic (Claude, the default and the most tested), OpenAI, Google Gemini, xAI Grok, DeepSeek, OpenRouter, or a local model through Ollama or LM Studio.

## Requirements

- Windows 10 (version 2004 or later) or Windows 11
- The [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), to build it
- An API key from one of the providers above. Claude works best: https://console.anthropic.com/settings/keys

## Install

1. Download or clone this repository.
2. Double-click **`Install Otto.cmd`**. It builds Otto, installs it to `%LOCALAPPDATA%\Programs\Otto`, adds Start menu and desktop shortcuts, and sets it to start with Windows.
3. On first start, Otto opens its settings. Choose a provider and paste your API key. The key is stored in Windows Credential Manager, never in a file.

Run `Install Otto.cmd` again any time to update after changing the code. **`Uninstall Otto.cmd`** removes Otto again. It keeps your notes and keys unless you run `uninstall.ps1 -All`.

Windows SmartScreen or your antivirus may warn about it the first time. The program isn't signed, and software that sends keystrokes and runs PowerShell looks suspicious to them. You're building it yourself from this source, so you can read exactly what it does.

## Using it

| Key | What it does |
|---|---|
| Ctrl+Shift+J | Open or hide the panel |
| Ctrl+Alt+J | Push-to-talk: press, speak, press again |
| Ctrl+Alt+End | **Stop everything immediately** |

The gear icon opens the settings: AI provider and key, models, email sign-in, reply animation and read-aloud.

Otto asks before anything with real consequences: buying or paying, signing something, submitting a formal document, sending an email, permanently deleting files, or running PowerShell that deletes things or changes Windows itself. Everything else, it just does. If it overwrites a file, the old version is backed up first.

## Safety

Please take these seriously:

- **Otto has real control of your PC.** It moves the real mouse and keyboard and can run commands. Keep **Ctrl+Alt+End** in mind, and don't leave it running unattended on things you care about.
- **Prompt injection.** A web page, document or email can contain text written to trick an AI ("ignore your instructions and..."). Otto is told to treat everything it reads as data, never as instructions, but no AI does that perfectly. Be careful letting it browse untrusted sites with your accounts signed in.
- **It never types passwords or card numbers**, by design. Sign in to things yourself.
- **Set a spending limit** with your AI provider (for Anthropic, in the console under Limits).

## Privacy

- What Otto sees (screen text, screenshots, files it reads, emails it opens) is sent to the AI provider you chose, so it can decide what to do. Close anything sensitive first, or use a local model.
- Your API keys and email sign-in are stored in Windows Credential Manager, encrypted by Windows. Otto never sees your email password: you sign in on Microsoft's own page.
- Notes, routines, custom sounds and file backups live in `%LOCALAPPDATA%\Otto`. Settings live in the registry under `HKCU\Software\Otto`.

## Costs

You pay your AI provider directly. With the default Claude setup (Haiku for everyday steps, Sonnet only when a task is hard), typical tasks cost about **1 to 3 US cents**. The panel shows a running total for each chat. Otto keeps costs down by reading the screen as text instead of pictures where it can, batching actions, caching, trimming old screen readings, and replaying saved routines without the AI.

## Email and calendar setup (Outlook / Hotmail)

Microsoft requires every app that reads Outlook mail to register itself once. It's free and takes about 5 minutes:

1. Go to the [Azure app registrations page](https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade) and sign in with any Microsoft account.
2. Click **New registration** and name it `Otto`.
3. Under **Supported account types**, choose *Accounts in any organizational directory and personal Microsoft accounts*.
4. Leave Redirect URI empty and click **Register**.
5. Open **Authentication**, set **Allow public client flows** to **Yes**, and save.
6. Copy the **Application (client) ID** into Otto's settings and click **Sign in**. You'll get a code to enter at microsoft.com/devicelogin.

Otto only asks for permission to use your mail and calendar.

## Customising

- **Sounds:** put `.wav` files in `%LOCALAPPDATA%\Otto\sounds` to replace any of Otto's sounds. The names are `send`, `reply`, `type`, `takeover`, `listen-on`, `listen-off`, `attention` and `error`. Restart Otto after adding them.
- **Free voice recognition:** turn on *Online speech recognition* in Windows Settings → Privacy → Speech. Otherwise voice needs Gemini or OpenAI as your provider, which transcribe it using a little of your API allowance.
- **Icons:** `assets/make_icons.py` draws them (it needs Pillow).

## Limitations

- It only sees and controls the main monitor.
- Windows doesn't let it click into apps running as administrator, or into UAC prompts.
- Providers other than Claude work, but get less testing, and some are weaker at multi-step PC control. DeepSeek can't see images.
- Some antivirus products may flag PowerShell commands that Otto runs.

## For developers

It's plain C# / WinForms on .NET 8, with no UI framework. Some useful entry points:

| File | What's in it |
|---|---|
| `Agent.cs` | The tool-use loop, model switching, history trimming and summaries |
| `Llm.cs` | Anthropic and OpenAI-compatible streaming connectors |
| `Desktop.cs`, `Input.cs`, `UiTree.cs` | Screenshots, mouse and keyboard, and reading windows as text |
| `Tools.cs`, `Apps.cs`, `Graph.cs`, `Memory.cs` | The tools Otto can call |
| `ChatPanel.cs`, `ChatView.cs`, `Overlay.cs` | The panel and the glow |

Debug switches (none of them use API credits, except `--api-test`):

```bash
Otto.exe --api-test "first message || second message"   # headless chat, log in %TEMP%\otto-api-test.txt
Otto.exe --dump-ui                                       # what Otto "sees" of the front window
Otto.exe --bench                                         # timings of the local work per step
Otto.exe --overlay-demo                                  # preview the glow
Otto.exe --settings                                      # just the settings window
```

## License

MIT. See [LICENSE](LICENSE).
