# Documentation build

Run `dotnet tool restore`, then `python3 scripts/build-docs.py` from the repository root.
The build requires the SDK in `global.json`, Python 3, and Node.js (22 in CI).
FsLiveDocs is pinned in `.config/dotnet-tools.json`. After rendering, the build uses Pagefind 1.5.2
to index the final site, including the custom homepage, and checks local links and fragments.

The build compiles the solution in Release, checks sample excerpts, executes every `docs/**/*.fsx`
file, runs `livedocs test`, and renders `output/`. It verifies that every visible F# example has
compiler tooltip data and that each tooltip target exists. Use `--no-build` after compiling Release locally.
Serve the result with `python3 -m http.server 8000 --directory output`.

## Authoring

Edit the files under `docs/`. `.livedocs/config.json` selects the five library API projects,
branding, navigation, and the generated documentation source directory. The additional
`.livedocs/examples/Examples.fsproj` supplies application dependencies for snippet compilation
(ASP.NET Core, Dapper, Expecto, Npgsql, and OpenTelemetry). It has no public API.

`build-docs.py` converts the existing literate `.fsx` pages to Markdown under `.livedocs/content/`.
That directory is disposable and ignored by Git. Literate comments become prose; ordinary code
becomes a code block; `(*** hide ***)` hides setup or assertions until the next prose comment.
The complete script is executed separately, so hidden assertions still fail the build. Other
literate directives are not supported. Add explicit conversion support before using one.

Keep `categoryindex` and `index` in page front matter. The adapter translates them into FsLiveDocs
folder and file ordering. It preserves the existing heading anchors and copies the custom homepage,
images, and legacy redirects. Numeric tutorial URLs redirect to FsLiveDocs' unnumbered paths.
The API index is `api/index.html`; `reference/index.html` and the old type-page names redirect there
or to the matching generated type. Old API member fragments may need to be selected again on the
new type page.

Ordinary `fsharp` fences are compiler checked together as a page. Use `isolated` for independent
alternatives that redeclare the same name. Keep every F# example checked: `no-check` only gives
lexical highlighting and removes compiler hover tips.

Partial examples use a matching `.livedocs/contexts/<page-path>.fs` template. Each `// snippet: N`
marker inserts the Nth F# fence from the page, preserving its code and applying the marker's
indentation. Surrounding imports, domain declarations, and function scopes become collapsible
FsLiveDocs `prepare` blocks. Every fence must occur exactly once, in order. `// snippet: N module`
turns a sample's file-scoped module into a nested module so later examples can reference it.
`// include: <repository-path>` imports a complete sample module without duplicating its source.
Contexts are compiler checked with the visible snippets; they are not executed. The complete
literate `.fsx` scripts are still executed separately, including their hidden assertions.

The `<!-- sample: ... -->` markers keep F# and C# excerpts synchronized with runnable samples.
Run `python3 scripts/check-learning-snippets.py --update` after changing sample regions.
FsLiveDocs does not compile C# fences; the sample build and behaviour checks in CI provide that
coverage. `<div class="cs-alt"></div>` pairs adjacent language alternatives in the rendered site.

## Verification and publishing

Run the complete documentation build and `git diff --check` before submitting a change.
The Docs workflow builds pull requests and deploys successful main-branch builds to GitHub Pages.
Serve `output/` locally to inspect both language tabs, nested navigation, code tooltips, and search.
The adapter supplies absolute project paths to work around FsLiveDocs 0.7.3's assembly-loading
failure with relative paths. Version 0.7.3 also treats static files as guides in documentation sets,
so the adapter copies assets after rendering. It adds missing record-field anchors to the generated
API tables. Remove these workarounds only after checking a newer pinned release.
