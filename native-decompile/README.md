# Native decompile output (local only, not committed)

This folder holds `.cs` files decompiled from the game's actual native `GameAssembly.dll` code
(via Cpp2IL's IL recovery, not just the structural interop stubs used everywhere else in this
repo). Unlike the interop stubs — which only expose method *signatures* — this recovers something
close to actual method *bodies*, i.e. a much closer derivative of the original game's source code.
Kept local-only and gitignored (see `.gitignore`) rather than committed, out of caution around
redistributing that.

## Regenerating

1. Download the latest Cpp2IL dev build for your platform (the tagged GitHub releases are too old
   to support this game's IL2CPP metadata version — you need a CI/nightly build):
   - Linux: https://nightly.link/SamboyCoding/Cpp2IL/workflows/dotnet-core/development/Cpp2IL-net9-linux-x64.zip
   - Windows: https://nightly.link/SamboyCoding/Cpp2IL/workflows/dotnet-core/development/Cpp2IL-net9-win-x64.zip
   - macOS: https://nightly.link/SamboyCoding/Cpp2IL/workflows/dotnet-core/development/Cpp2IL-net9-osx-x64.zip
2. Run it against the game folder with IL recovery output:
   ```sh
   ./Cpp2IL --game-path "/path/to/Dave the Diver" --output-as dll_il_recovery --output-to ./out
   ```
   Takes about a minute; produces `./out/Assembly-CSharp.dll` (and every other game/plugin DLL)
   with recovered (if imperfect — some methods come out with garbled Vector2/Vector3 struct
   handling, "Expected O, but got Ref"-style comments, etc.) method bodies instead of empty stubs.
3. Decompile whatever type you need from that DLL with `ilspycmd` (already used elsewhere in this
   repo's workflow):
   ```sh
   ilspycmd -t Namespace.TypeName ./out/Assembly-CSharp.dll > TypeName_native.cs
   ```

## Caveats

Cpp2IL's IL recovery is not perfect, especially around struct/SIMD-heavy code (Vector2/Vector3
field access, in particular). Treat the recovered code as a strong *hint* at the real logic and
control flow, not as ground truth — verify anything load-bearing by testing in-game rather than
trusting the decompile at face value.
