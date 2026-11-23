# TextMate grammars and editor themes

The main catalog comes from [shikijs/textmate-grammars-themes](https://github.com/shikijs/textmate-grammars-themes).
The bundled metadata pins payload URLs to a repository commit, so browsing needs no network and each application version has a reproducible catalog.
The current snapshot contains 242 languages, 18 supporting grammars, and 65 themes.

Settings → Editor offers searchable grammar downloads and theme downloads. Download a theme, then select it for the dark or light editor theme. Downloads are optional; built-in languages and Codale's default theme work offline.

`.env`, `.env.*`, `*.env`, and `*.env.*` filenames use installed dotenv grammars, with INI as a fallback, including local, production, and example variants. No additional grammar is bundled or automatically downloaded. Exact filename associations and per-file language overrides retain their normal precedence.

Grammar installation follows the catalog's embedded-language and injection dependencies, including cycles, downloading and validating every missing payload before writing any of them. Supporting grammars are stored for scope resolution without claiming file associations or appearing as installed user languages. Selecting a supporting language explicitly promotes it to an installed language. Existing installed dependencies are retained. A disk-write failure may leave some validated dependencies installed; network and validation failures leave the store unchanged.

Associations come from grammar-declared metadata, the publishers' VS Code extension manifests, a pinned [GitHub Linguist](https://github.com/github-linguist/linguist/blob/main/lib/linguist/languages.yml) snapshot, supplemental mappings for grammar variants, and existing TextMateSharp language metadata. The Shiki repository remains the primary language listing and grammar payload source. Every listed standalone language has association metadata; injection dependencies remain excluded from file detection.

Existing installations are enriched in memory by language id and scope, so old downloads with empty association fields work without reinstalling, fetching anything, or rewriting their manifests. New installations persist the enriched metadata. Grammar-declared metadata is cached by payload modification time. Exact filenames take precedence, followed by the dotenv family, filename globs, longest dotted extension, and bounded first-line matching. Patterns support `*`, `?`, character classes, `**` path segments, and brace alternatives. Installed languages take precedence over shipped ones within each detection tier. Shared extensions can be ambiguous; per-file language overrides choose the intended grammar. Imported custom grammars retain their declared file types, filenames, patterns, and first-line hints. TextMateSharp compatibility normalization handles capture metadata, repository pattern arrays, and begin-only rules when loading, while preserving original payloads on disk.

The tokenizer preserves complete outer-to-inner scope stacks, including otherwise plain tokens, and merges adjacent spans only when their scopes also agree. Themes resolve language-specific scopes, ancestor paths, direct-child selectors, alternatives, grouped alternatives, and exclusions. Foreground, background, and font style inherit independently; more specific selectors win, and equal-specificity ties use the last rule. Explicit empty font styles clear inherited styles. Bold, italic, underline, and strikethrough render in the editor, fenced code, and search snippets. Scope matches are cached independently of the incremental grammar cache, so changing a theme does not require re-tokenizing files.

The editor applies theme background, foreground, gutter, line-number, caret, selection, active-line, occurrence, and find-highlight colors. Token backgrounds sit beneath selection and search overlays. Fenced code uses the theme's editor foreground/background. RGB, RGBA, and shorthand colors preserve alpha; swap-chain backgrounds are composited to an opaque surface. Global legacy `settings` and VS Code `tokenColors` are supported. Catalog payloads are already flattened; theme files containing unresolved `include` paths are rejected rather than partially applied. Separate dark/light settings follow app appearance and are saved in the application database. Downloaded theme JSON lives under the syntax store's themes directory.

To refresh the snapshot from a local checkout:

```powershell
npm ci --prefix scripts
node scripts/refresh-textmate-associations.mjs C:/path/to/textmate-grammars-themes
node scripts/update-textmate-catalog.mjs C:/path/to/textmate-grammars-themes
```

The association refresh reads publisher manifests at the source revisions recorded by Shiki and writes only metadata to `scripts/textmate-associations.json`. Its Linguist revision is pinned in the refresh script. The regular catalog generator can reuse that checked-in metadata snapshot offline. Association source URLs and license notices are retained. Neither script adds grammar payloads to the application.

Review the generated metadata and run the Core and Settings tests plus an App build. The script also updates upstream license notices copied into the application output. Individual source and license URLs are shown in download tooltips.
