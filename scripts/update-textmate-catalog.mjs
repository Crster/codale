// Usage: node scripts/update-textmate-catalog.mjs <checkout of shikijs/textmate-grammars-themes>
// Only metadata is bundled; grammar and theme payloads are downloaded on demand.
import { readFile, writeFile, copyFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { execFileSync } from 'node:child_process';

const root = resolve(process.argv[2]);
const revision = execFileSync('git', ['-C', root, 'rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
const { grammars, injections } = await import(pathToFileURL(resolve(root, 'packages/tm-grammars/index.js')));
const { themes } = await import(pathToFileURL(resolve(root, 'packages/tm-themes/index.js')));
const base = `https://raw.githubusercontent.com/shikijs/textmate-grammars-themes/${revision}/packages`;
const out = 'src/Codale.Core/Syntax/Bundled';
const associations = JSON.parse(await readFile(new URL('./textmate-associations.json', import.meta.url), 'utf8'));
const unique = values => [...new Set(values.filter(Boolean))];
const glob = value => ['*', '?', '[', '{', '/', '\\'].some(character => value.includes(character));
const escapeRegex = value => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const entries = [];
// Associations absent from upstream grammar files; shipped TextMateSharp associations are also preserved at install time.
const extensions = { vue: ['vue'], svelte: ['svelte'], astro: ['astro'], zig: ['zig', 'zon'],
  terraform: ['tf', 'tfvars'], hcl: ['hcl'], graphql: ['graphql', 'graphqls', 'gql'],
  prisma: ['prisma'], gdscript: ['gd'], nim: ['nim', 'nims', 'nimble'], d: ['d', 'di'],
  dotenv: ['env'], toml: ['toml'], typescript: ['ts', 'mts', 'cts'], tsx: ['tsx'], jsx: ['jsx'],
  logo: ['logo'], 'vue-vine': ['vine.ts'], wenyan: ['wy'], 'ts-tags': ['ts', 'mts', 'cts'],
  'vue-html': ['html', 'htm'], 'nextflow-groovy': ['nf'],
  javascript: ['js', 'mjs', 'cjs'], jsonc: ['jsonc'], json5: ['json5'],
  yaml: ['yaml', 'yml'], markdown: ['md', 'markdown'], shellscript: ['sh', 'bash', 'zsh'] };
for (const g of [...grammars, ...injections]) {
  const raw = JSON.parse(await readFile(resolve(root, `packages/tm-grammars/grammars/${g.name}.json`), 'utf8'));
  const association = injections.includes(g) ? {} : associations[g.name] ?? {};
  entries.push({ id: g.name, name: g.displayName ?? g.name, scopeName: g.scopeName,
    extensions: unique([...(raw.fileTypes ?? []), ...(association.extensions ?? []), ...(extensions[g.name] ?? [])].filter(t => !glob(t))).map(e => e.replace(/^\./, '').toLowerCase()),
    aliases: unique([...(g.aliases ?? []), ...(association.aliases ?? [])]),
    dependencies: [...new Set([...(g.embedded ?? []), ...injections.filter(i => i.embeddedIn?.includes(g.name)).map(i => i.name)])],
    filenames: unique([...(raw.fileTypes ?? []).filter(t => !glob(t)), ...(association.filenames ?? []).filter(t => !glob(t)),
      ...(g.name === 'dotenv' ? ['.env'] : g.name === 'docker' ? ['Dockerfile', 'Containerfile'] : g.name === 'make' ? ['Makefile', 'GNUmakefile'] : [])]),
    filenamePatterns: unique([...(raw.fileTypes ?? []).filter(glob), ...(association.extensions ?? []).filter(glob),
      ...(association.filenames ?? []).filter(glob), ...(association.filenamePatterns ?? [])])
      .map(p => g.name === 'dotenv' && p === '.env*' ? '.env.*' : p),
    firstLine: raw.firstLineMatch ?? association.firstLine ?? (association.interpreters?.length
      ? `^#!.*\\b(?:${association.interpreters.map(escapeRegex).join('|')})(?:\\s|$)` : null),
    associationSources: association.sources ?? [],
    hidden: injections.includes(g), injectionScopes: raw.injectTo ?? g.injectTo ?? [],
    url: `${base}/tm-grammars/grammars/${g.name}.json`, source: g.source,
    license: g.license ?? '', licenseUrl: g.licenseUrl ?? '' });
}
await writeFile(`${out}/catalog.json`, JSON.stringify(entries, null, 2) + '\n');
await writeFile(`${out}/themes.json`, JSON.stringify(themes.map(t => ({
  id: t.name, name: t.displayName, type: t.type,
  url: `${base}/tm-themes/themes/${t.name}.json`, source: t.source,
  license: t.license ?? '', licenseUrl: t.licenseUrl ?? '',
})), null, 2) + '\n');
for (const [pkg, name] of [['tm-grammars', 'grammars'], ['tm-themes', 'themes']])
  await copyFile(resolve(root, `packages/${pkg}/NOTICE`), `${out}/${name}.NOTICE`);
await copyFile(resolve(root, 'packages/tm-grammars/LICENSE'), `${out}/shiki.LICENSE`);
console.log(`Updated ${entries.length} grammars and ${themes.length} themes at ${revision}`);
