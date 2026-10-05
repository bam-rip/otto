# Privacy

Otto runs on your PC. It has no account, no server of its own, no analytics and no tracking. This is every
connection it makes and what it sends.

## Only when you ask it to do something

- **Your AI provider** (Anthropic, OpenAI, Google, xAI, DeepSeek, OpenRouter, or a server you set up). Each request
  sends your message and what Otto needs to act on it: text it read from the screen, screenshots, the contents of
  files, web pages or emails it opened, and the conversation so far. Voice messages are sent for transcription when
  Windows' own speech recognition isn't on. What the provider does with it is covered by their privacy policy. A local
  model (Ollama, LM Studio) keeps all of it on your PC.
- **Web search and web pages**: searches go to DuckDuckGo or Bing; pages Otto reads are fetched from their sites.
- **Your email provider** (Gmail and other IMAP services, or Microsoft for Outlook), once you connect it in settings.

## On its own

- **Update check, once a day**: Otto asks GitHub (api.github.com) whether there's a newer release. That request
  contains nothing about you beyond what any web request does (your IP address). When a new version is found it is
  downloaded from GitHub. Turn this off in Settings → Updates.

## What stays on your PC

- Chats (encrypted with your Windows account), notes, routines, backups and sounds: `%LOCALAPPDATA%\Otto`
- Settings: the registry under `HKCU\Software\Otto`
- API keys, app passwords and email sign-ins: Windows Credential Manager

Tray → **Uninstall Otto…** removes the program, and can remove all of the above too.
