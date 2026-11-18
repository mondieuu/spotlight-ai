# Spotlight AI

ChatGPT and Gemini behind a live Liquid Glass search box on Windows. Press **Alt + Shift + Space** (or turn on the Windows key option), type, press Enter: the panel grows, your text goes to the service you picked, and the answer appears right there.

- **Your own account:** each service opens on its own official website inside the panel, and you sign in there. Free accounts work too, and no API key is needed.
- **Clean on every open:** closing the panel empties the field, and your next question starts a new conversation. Past chats stay in the service's own history.
- **Private by design:** the app does not read or store your passwords or conversations. It only places what you typed into the site's input and presses its send button. What you write goes to the service you chose, under its terms. Sign-ins stay in a local browser profile and are removed on uninstall.
- **Live glass:** whatever is behind the panel keeps moving. The panel still shows in screenshots and streams such as Discord, so what you type is visible to viewers.

You use these services under their own terms. Sites can change their pages, which can stop text from being sent automatically. Your text then stays in the box and you can use the site's own input.

Requires Windows 10 2004 or later and the Microsoft Edge WebView2 Runtime, which most PCs already have.

Not affiliated with or endorsed by OpenAI, Google or Apple. ChatGPT, Gemini and Spotlight are trademarks of their owners.

## Install

Download **[SpotlightAISetup.exe](https://github.com/mondieuu/spotlight-ai/releases/latest/download/SpotlightAISetup.exe)** and run it: a standard setup wizard installs Spotlight AI into Program Files, so Windows asks for administrator approval. Settings and sign-ins stay with each Windows user. Uninstall it from **Settings › Apps**. The file is not digitally signed, so SmartScreen may warn: **More info → Run anyway**.

## Build

```bash
dotnet publish -c Release -r win-x64 -o publish
```

Then build the setup wizard with [NSIS 3](https://nsis.sourceforge.io):

```bash
makensis /DVERSION=1.2.0 installer\SpotlightAI.nsi
```

This writes `publish\SpotlightAISetup.exe`.
