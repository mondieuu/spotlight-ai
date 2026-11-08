# Spotlight AI

Ask AI from a live Liquid Glass search box on Windows. Press **Alt + Shift + Space** (or turn on the Windows key option), type, press Enter, and the answer streams in right below.

- **Your own OpenRouter key:** paste it in Settings and pick any model from [openrouter.ai/models](https://openrouter.ai/models). It is encrypted for your Windows user with DPAPI and only sent to OpenRouter.
- **Nothing is kept:** closing the panel forgets the conversation, and conversations are never written to disk. What you ask goes to OpenRouter and the model you chose, under their terms.
- **22 themes:** the same themes as the Mondium browser.
- **Live glass:** whatever is behind the panel keeps moving. The panel still shows in screenshots and streams such as Discord, so what you type is visible to viewers.

Requires Windows 10 or later. Not affiliated with or endorsed by OpenRouter or Apple. Spotlight is a trademark of Apple Inc.

## Build

```bash
dotnet publish -c Release -r win-x64 -o publish
```

Rename `publish\SpotlightAI.exe` to `SpotlightAISetup.exe`: an exe with "Setup" in its name opens as the installer.
