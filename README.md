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
headings, lists, and code) and LaTeX math (`$...$`, `\(...\)`, `$$...$$`,
and `\[...\]`) without a bubble. Invalid or unfinished math remains visible
as source text. Assistant replies reveal one character at a time; adjust
ReplyCharacterIntervalMs in MainWindow.xaml.cs to change the pace. On the Chat
Completions path, a direct answer makes an additional final-answer request
after the agent's tool-decision request.

Click the model name in the composer to open the model list. Choosing a model with
adjustable reasoning changes the menu to a vertical list of reasoning levels;
choose a level to close it, or go back to the model list. Each model remembers its
reasoning selection for the current app session. The menu is locked during a response.
The selected level appears in gray beside the white model name; None leaves it blank. GPT-5 offers
Default, Minimal, Low, Medium, and High. GPT-6 Sol/Luna offer Default, None,
Low, Medium, High, XHigh, and Max; GPT-6 Astra and GPT-6.1 Sol offer the same
choices except None. Default omits the API parameter. GPT-4.1 mini has no
adjustable reasoning. Hover over a model choice for its reasoning hint.
The context and Clear files row appears only while files are attached to the chat.

`AgentModelOption.ReasoningEffort` is a nullable `ReasoningEffort` enum with None,
Minimal, Low, Medium, High, XHigh, and Max. Configuration keeps readable strings
such as `"ReasoningEffort": "medium"`; omit the property or use null for Default.
Unknown values, numbers, and levels incompatible with the model are rejected.
New model IDs need an allowed-level mapping in `AgentModelOption` before their
reasoning choices appear.

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

Lavender uses the Responses API for GPT-6 agent turns and Chat Completions for
GPT-5 and GPT-4.1 mini. GPT-6 requests set `store: false` and replay returned
reasoning and tool items within the turn. The default GPT-6 Sol configuration
still selects None; choose another level from the menu when needed.

A missing-key message names the environment variable to set. A model-access error
requires checking the exact model ID, project permissions, and API billing in your
OpenAI account; a new key alone does not enable a model.

References: [API authentication](https://developers.openai.com/api/reference/overview),
[GPT-6 compatibility](https://developers.openai.com/api/docs/guides/latest-model).
