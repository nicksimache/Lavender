pip install -r requirements.txt

icons from icon-icons.com, svgrepo.com,

## Chat models and API keys

The chat composer has a model dropdown: GPT-4.1 mini, GPT-5, GPT-6 Sol (the startup
default), GPT-6.1 Sol, GPT-6 Luna, and GPT-6 Astra. The choice applies to the next
message and keeps the current conversation and attached project files. Enter sends;
Shift+Enter adds a line.
The arrow becomes a stop button during a response. Use + to add project files.
User messages use compact, right-aligned solid-color bubbles capped at 70% of the
chat width. Assistant replies render common Markdown (including bold, italics,
headings, lists, and code) without a bubble. Streamed replies reveal one character
about every 24 ms. A direct answer currently makes one additional
final-answer request after the agent's tool-decision request, so streaming can
increase API usage for messages that need no tools.

Click the model name in the composer to open the model menu and its **Reasoning**
slider. The slider remembers a separate selection for each model for the current
app session. It snaps to valid levels and is locked while a response is running.
GPT-5 offers Default, Minimal, Low, Medium, and High.
Default omits the API parameter; it is different from None. GPT-4.1 mini has no
adjustable reasoning. GPT-6 Sol/Luna are fixed at None for the current tool flow.
Hover over a disabled control to see why it is unavailable.
The context and Clear files row appears only while files are attached to the chat.

`AgentModelOption.ReasoningEffort` is a nullable `ReasoningEffort` enum with None,
Minimal, Low, Medium, High, XHigh, and Max. Configuration keeps readable strings
such as `"ReasoningEffort": "medium"`; omit the property or use null for Default.
Unknown values, numbers, and levels incompatible with the model's current tool
integration are rejected. New model IDs need an allowed-level mapping in
`AgentModelOption` before their slider can be enabled.

All configured models use `OPENAI_API_KEY` by default. OpenAI keys belong to a project,
not an individual model: your existing key can be used for any model that project
can access, with API billing enabled. No new key is needed just to switch models.

To set or replace the key on Windows:

1. Create a key in the [OpenAI API dashboard](https://platform.openai.com/api-keys).
2. Open **Edit environment variables for your account** from the Start menu.
3. Under **User variables**, create or edit `OPENAI_API_KEY` and paste the key as its value.
4. Fully close and reopen Lavender. If launching from Visual Studio or a terminal,
   restart that application too so it receives the updated environment.

Never put the actual key in `appsettings.json` or commit it to source control.

### Separate keys or different model IDs

Edit `Agent.Models` in `appsettings.json`. Each entry has a display `Label`, the
exact API model `Id`, and an `ApiKeyEnvironmentVariable` naming the variable to read.
For separate credentials, create user environment variables `OPENAI_API_KEY_GPT5`
and `OPENAI_API_KEY_GPT6` using the steps above, then set those names on the
corresponding model entries. The variable name is not the secret key itself.

`Agent.Model` sets the startup default and must match an entry's `Id`. Rebuild after
editing the source configuration, or edit `appsettings.json` beside the deployed
executable and restart. The dropdown selection lasts for the current app session.

Lavender currently uses Chat Completions with function tools. GPT-6 Sol and Luna are
configured with `ReasoningEffort: "none"` for compatibility with that tool flow.
GPT-6 Astra and GPT-6.1 Sol appear in the model menu, but selecting them for a
tool-enabled chat shows a compatibility message: they require a Responses API
implementation before they can run the agent's tools.

A missing-key message names the environment variable to set. A model-access error
requires checking the exact model ID, project permissions, and API billing in your
OpenAI account; a new key alone does not enable a model.

References: [API authentication](https://developers.openai.com/api/reference/overview),
[GPT-6 compatibility](https://developers.openai.com/api/docs/guides/latest-model).
