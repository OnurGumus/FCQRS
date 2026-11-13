# Documentation build

Run `dotnet tool restore`, then `python3 scripts/build-docs.py` from the repository root.
The build requires the SDK in `global.json`, Python 3, and Node.js (22 in CI).
FsLiveDocs is pinned in `.config/dotnet-tools.json`. After rendering, the build uses Pagefind 1.5.2
to index the final site, including the custom homepage, and checks local links and fragments.
Pagefind indexes only `data-pagefind-body` elements. The build marks each generated page's `<main>`
and the homepage marks its own, so navigation and redirect pages stay out of search results.

The build compiles the solution in Release, checks sample excerpts, executes every `docs/**/*.fsx`
file, runs `livedocs test`, and renders `output/`. It verifies that every visible F# example shows
the code written in `docs/` and has compiler tooltip data, and that each tooltip target exists.
Use `--no-build` after compiling Release locally.
Serve the result with `python3 -m http.server 8000 --directory output`.

## Authoring

Edit the files under `docs/`. `.livedocs/config.json` selects the six library API projects,
branding, navigation, and the generated documentation source directory. The additional
`.livedocs/examples/Examples.fsproj` supplies application dependencies for snippet compilation
(ASP.NET Core, Dapper, Expecto, Npgsql, and OpenTelemetry). It has no public API.

`build-docs.py` converts the existing literate `.fsx` pages to Markdown under `.livedocs/content/`.
That directory is disposable and ignored by Git. Literate comments become prose; ordinary code
becomes a code block; `(*** hide ***)` hides setup or assertions until the next prose comment.
The complete script is executed separately, so a hidden check that throws fails the build. Do not
use `assert` for these checks: `dotnet fsi` compiles it away. Other
literate directives are not supported. Add explicit conversion support before using one.

Keep `categoryindex` and `index` in page front matter. The adapter translates them into FsLiveDocs
folder and file ordering. It builds heading anchors with the fsdocs rule, joining the heading's words
with `-` and skipping inline code, so existing deep links still resolve. It also copies the custom
homepage, images, and legacy redirects. Numeric tutorial URLs redirect to FsLiveDocs' unnumbered paths.
The API index is `api/index.html`; `reference/index.html` and the old type-page names redirect there
or to the matching generated type. Old API member fragments may need to be selected again on the
new type page.

Ordinary `fsharp` fences are compiler checked together as a page. Use `isolated` for independent
alternatives that redeclare the same name. Keep every F# example checked: `no-check` only gives
lexical highlighting and removes compiler hover tips.

Partial examples use a matching `.livedocs/contexts/<page-path>.fs` template. Each `// snippet: N`
marker inserts the Nth F# fence from the page at the marker's indentation. Surrounding imports,
domain declarations, and function scopes become collapsible FsLiveDocs `prepare` blocks. Every fence
must occur exactly once, in order. `// snippet: N module` turns a sample's file-scoped module into a
nested module so later examples can reference it. The compiler checks the indented or nested form;
after rendering, the build removes the added indentation and the module's `=`, so readers see each
fence as written.
`// include: <repository-path>` imports a complete sample module without duplicating its source.
Contexts are compiler checked with the visible snippets; they are not executed. The complete
literate `.fsx` scripts are still executed separately, including their hidden assertions.

The `<!-- sample: ... -->` markers keep F# and C# excerpts synchronized with runnable samples.
Run `python3 scripts/check-learning-snippets.py --update` after changing sample regions. The check
reads the tutorial, the task guides, and each concept page that has a marker. The check
also fails when a code block has a line wider than it shows at desktop size: 80 columns for F# and C#
in the tutorial and task guides, and 72 for the tutorial's other blocks, which sit in the text column.
Task guides may quote wider logs and shell commands, and the workflow tutorial's `text` blocks may
quote wider program output. `content/site.css` gives paired C#
examples the same frame as F# examples.

The workflow tutorial (`docs/workflow-tutorial/`) shows every line of its programs
(`samples/orders/`). The region name `file` quotes a whole file without its `// docs:` markers, for
project files and `global.json`. For those samples the check also fails when a non-blank source
line is in no region, and when a region or project file that is new or changed since the previous
step is quoted on no page. A step page puts `Order.fs` changes before `Program.fs` changes, because
the compiler checks fences in page order; its `.livedocs/contexts/` template declares the unchanged
types the shown fences use. `scripts/check-orders.py` runs each step from a clean copy, F# and C#,
and compares the output with what the page shows.

FsLiveDocs names a sidebar section after its folder. `build-docs.py` renames the two tutorial
sections to "Tutorial: CQRS" and "Tutorial: Workflows" after rendering (`SECTION_LABELS`).
FsLiveDocs does not compile C# fences; the sample build and behaviour checks in CI provide that
coverage. highlight.js colours a C# type name only where it is declared, so
`content/language-tabs.js` colours the other type names from the code around them, without a
compiler. It can miss a type name or colour a property that stands where a type could.
`<div class="cs-alt"></div>` pairs adjacent language alternatives in the rendered site.
A page address with `?lang=csharp` (or `cs`) opens its examples in C#, and `?lang=fsharp` (or `fs`)
in F#. Choosing a tab writes the choice into the address, so the address bar can be copied as a
link.
An F# example's collapsed setup stays in its F# tab, so it is hidden when C# is selected.
F# examples and samples write a multi-line record, list, object expression, or computation
expression in Stroustrup style: the opening brace or bracket ends a line, and the closing one
stands on its own line, aligned with the start of that line. A copy-and-update in a match arm keeps
`{ state with` on the line after the arrow and closes in the same column as its opening brace,
because a closing brace under the match bar does not compile.

## Verification and publishing

Run the complete documentation build and `git diff --check` before submitting a change.
The Docs workflow builds pull requests and deploys successful main-branch builds to GitHub Pages.
Serve `output/` locally to inspect both language tabs, nested navigation, code tooltips, and search.
The adapter supplies absolute project paths to work around FsLiveDocs 0.7.3's assembly-loading
failure with relative paths. Version 0.7.3 also treats static files as guides in documentation sets,
so the adapter copies assets after rendering. It adds missing record-field anchors to the generated
API tables. Remove these workarounds only after checking a newer pinned release.
