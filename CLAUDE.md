# Working on Horizon

Notes from bogz for whoever (or whatever) writes code and commits in here.

## Voice

Comments and commit messages read like the early development commits, not like a press release. Casual, a bit
intellectual, the odd swear word where it earns its keep ("fuck it we ball", "a blurry pile of shit", "galactus
allocation", "no nullptr c#!!!! woww!!!!"). Short commit titles that say what happened ("Horizon.Physics now works!",
"so theres an interpreted language now", "Removed vestigial projects... :("). Explain the why in plain sentences,
lowercase inline comments are fine, exclamation marks are fine, a joke is fine.

Avoid the AI sounding colon construction ("Thing: explanation", "Rendering: ...") in comments, docs and commit
messages. Write a sentence instead. No bullet lists of "Added X" in commit bodies either, say it like a person.

## Where the rendering is going

The rendering logic is to be kept apart from the OpenGL backend, so a different backend (Vulkan is the one in mind)
can be dropped in later. Everything outside `Horizon/OpenGL` talks to the GPU through `Horizon.Graphics`
(`GraphicsDevice` and the resource classes), never through Silk's `GL` directly. New rendering code goes through the
device; anything that needs a GL call the device hasn't got gets the device extended, not a `GL.` sprinkled into a
renderer. What is still GL flavoured and has to go before a second backend exists is listed in
`docs/project-health-2.md` under "Towards a second backend".

## Building and testing

- `dotnet build Horizon.sln -p:EnableWindowsTargeting=true` on Linux (the HIDL editor is WinForms).
- `dotnet test Horizon.Tests`.
- Headless on Linux, with Xvfb and Mesa, `MESA_GL_VERSION_OVERRIDE=4.6 MESA_GLSL_VERSION_OVERRIDE=460 ./Horizon.Testing <scene>`,
  `HORIZON_INPUT_SCRIPT` to quit after a while, `HORIZON_LOG_LOOPS=1` for the numbers, `HORIZON_SCREENSHOT=file.png@3`
  to see what was drawn.
