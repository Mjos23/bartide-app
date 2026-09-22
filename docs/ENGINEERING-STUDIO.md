# Engineering Studio

Engineering Studio is a beginner practice workspace at `/engineering/index.html` on the demo site. It pairs short C# lessons with a browser editor, notebook prompts, and a separate HTML/CSS/JavaScript prototype preview. It is a learning surface; changes made in its editors do not modify the TideCasa application source or deploy application changes.

## Learning workflow

1. Choose a lesson and read its goal and expected output.
2. Predict the result in the notebook before changing the code.
3. Read the complete `Program.cs`, then make one small change.
4. Export the C# project ZIP, extract it to a local folder, and run `dotnet run` in that folder using a .NET 10 SDK.
5. Compare the actual output with the expected output. Follow the lesson's deliberate bug step, read the failing check, restore the fix, and run again.
6. Record the observed behavior and evidence in the notebook. Continue with the challenge when the original checks pass.

The C# editor contains real standalone console programs. C# is not compiled or executed by the browser or demo server. Expected output is a reference supplied with the lesson, not evidence that edited C# has run. The downloaded project is the execution path for C#. Completion checkmarks record the learner's progress; they do not certify a passing local run.

The prototype preview runs HTML, CSS, and JavaScript in the browser. Use it to explore a screen or interaction before adapting the idea into reviewed application code. A successful browser preview does not establish that an equivalent C# implementation is correct.

## Static client architecture

The studio is served from `TideCasa.Blazor/wwwroot/engineering/`. Its entry point is static `index.html`, with client-side JavaScript driving the editors and lesson controls. `csharp-lessons.json` supplies five ordered lessons and their source, expected output, notebook prompts, and repository references.

The studio loads its own static assets and lesson data. It has no application API integration, database connection, or server-side code execution. The JavaScript prototype is assembled into a `srcdoc` iframe with `sandbox="allow-scripts"`; it does not receive `allow-same-origin`. The surrounding page's content security policy permits inline frame documents but limits URL frame sources to `blob:`, while the preview's own policy blocks external resource loads, HTTP connections, forms, and nested frames. These controls separate edited prototype code from the surrounding studio document and its origin's storage, and block navigation to HTTP pages. Browser verification confirmed parent DOM access, preview localStorage, HTTP fetch and navigation to the local health page were blocked. A runaway synchronous loop can still occupy the browser; reload the studio and restore its starter without running that draft again. Edited drafts are not automatically executed on reload.

Drafts and progress are saved in browser `localStorage`. Before saving, a tab compares the currently stored draft with the snapshot it last observed. If another tab changed that draft, saving pauses so the older tab cannot silently overwrite newer work; export its current work before reloading to pick up the stored version. This is browser-local persistence, not an account-backed save or synchronization service. JSON import/export carries studio work between sessions or devices. The C# project ZIP export is generated in the browser and packages the current C# source for local use. Importing work or editing a draft does not update the repository.

Production work remains an engineer-reviewed step: select the intended behavior, implement it in the appropriate application layer, validate it against repository requirements, and use the established deployment process. The studio is not a deployment control panel.

## Lesson scope and repository connections

All examples use fictional restaurant and order values, in-memory state, and the .NET standard library. They require no external packages, credentials, network access, or application database. Every `Program.cs` contains a `Check` method that throws when an invariant fails and prints `PASS` only after its checks succeed.

| ID | Concept and checked behavior | Related application source |
| --- | --- | --- |
| `money` | Whole-cent totals, explicit decimal rounding, a half-cent boundary, and zero tax/tip | `TideCasa.Contracts/RestaurantOrdering.cs`; `TideCasa.Api/Features/RestaurantOrdering/RestaurantOrderingStore.cs` |
| `validation` | Required name and accepted/rejected quantity boundaries; independent problems reported together | `TideCasa.Api/Features/RestaurantOrdering/RestaurantOrderingStore.cs`; `TideCasa.Blazor/Features/Ordering/RestaurantOrderingFlow.cs` |
| `records` | Record value equality and `with` copies that preserve the original | `TideCasa.Contracts/RestaurantOrdering.cs`; `TideCasa.Domain/CustomerRewards/RewardRule.cs` |
| `tenants` | Matching both tenant and order identifiers; absent tenants/orders produce no result | `TideCasa.Api/Infrastructure/TenantStaffAccess.cs`; `TideCasa.Api/Features/RestaurantOrdering/RestaurantOrderingStore.cs` |
| `retries` | Identical retries reuse a receipt; changed payloads conflict; tenant keys remain separate | `TideCasa.Contracts/RestaurantOrdering.cs`; `TideCasa.Api/Features/RestaurantOrdering/RestaurantOrderingStore.cs` |

These are concept-sized exercises, not drop-in production components. The money lesson states a fictional tax rate and rounding policy. The tenant lesson teaches filtering and does not authorize a user. The retries lesson uses an in-memory dictionary and does not handle durability or concurrent requests. The records lesson uses immutable scalar values; a `with` copy of a record containing a mutable collection would still share that collection unless explicitly copied.

## Lesson data contract

The JSON root is an object with a `lessons` array. Each lesson includes:

- `id`, `title`, and `minutes` for selection and pacing;
- `goal`, `explanation`, and ordered `steps` for guided work;
- `notebook` for editable prediction and debugging notes;
- `source` containing the entire standalone `Program.cs`;
- `expected` containing the original program's exact console output;
- `challenge` for the next small extension;
- `sourcePaths` pointing to existing repository files for deeper reading.

The stable lesson order is `money`, `validation`, `records`, `tenants`, `retries`. Source references identify related real code; they do not cause the lessons to import or execute that code.

## Verification

Lesson verification uses the repository's local .NET 10.0.401 SDK in `.tools/dotnet-10.0.401/dotnet.exe`. Each source is copied to an isolated console project under `.tools/engineering-lessons/verification/` targeting `net10.0`. Restore uses an empty package-source list, followed by `dotnet run --no-restore`; no NuGet package download is needed.

The verifier checks the JSON shape, lesson order, and existence of every referenced source path. For each lesson it compares the complete console output to `expected`, introduces one deliberate bug, requires a nonzero exit with the intended failure message, then restores the source and requires the original passing output again. The deliberate defects exercise rounding, the zero-quantity boundary, the copied price, tenant filtering, and payload comparison during retry.

Run the lesson verification with `python scripts/verify-engineering-lessons.py`. It writes per-run logs and `results.json` beneath the isolated verification directory. These checks verify the downloadable C# examples. Browser rendering, preview isolation, import/export behavior, persistence, and hosted delivery require separate integrated checks of the studio implementation.
