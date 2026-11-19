// Refresh only language-association metadata from the grammar publishers' VS Code manifests.
// Usage: node scripts/refresh-textmate-associations.mjs <Shiki repository checkout>
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { tmpdir } from 'node:os';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import { parse as parseYaml } from 'yaml';

const root = resolve(process.argv[2]);
const { grammars } = await import(pathToFileURL(resolve(root, 'packages/tm-grammars/index.js')));
const cache = join(tmpdir(), 'codale-association-manifests');
await mkdir(cache, { recursive: true });
const requests = new Map();
async function manifest(url) {
  if (!requests.has(url)) requests.set(url, (async () => {
    const file = join(cache, createHash('sha256').update(url).digest('hex') + '.json');
    let text;
    try { text = await readFile(file, 'utf8'); }
    catch {
      const response = await fetch(url, { signal: AbortSignal.timeout(15000) });
      if (response.status === 404) return null;
      if (!response.ok) throw new Error(`${response.status}: ${url}`);
      text = await response.text();
      await writeFile(file, text);
    }
    try { return JSON.parse(text); } catch { return null; }
  })());
  return requests.get(url);
}

const result = {};
// Linguist contributes association metadata only; Shiki remains the grammar list and payload source.
const linguistRevision = '14ac04abe3e3722e42c52489b377d9d4118fc8b6';
const linguistSource = `https://raw.githubusercontent.com/github-linguist/linguist/${linguistRevision}/lib/linguist/languages.yml`;
const linguistResponse = await fetch(linguistSource);
if (!linguistResponse.ok) throw new Error(`Could not read ${linguistSource}`);
const linguist = Object.entries(parseYaml(await linguistResponse.text())).map(([name, meta]) => ({ name, ...meta }));
const key = value => value.toLowerCase().replace(/[ _]/g, '-');
for (const g of grammars) {
  const names = [g.name, g.displayName, ...(g.aliases ?? [])].filter(Boolean).map(key);
  const named = linguist.filter(l => [l.name, ...(l.aliases ?? [])].map(key).some(n => names.includes(n)));
  const scoped = linguist.filter(l => l.tm_scope === g.scopeName);
  const matches = named.length === 1 ? named : scoped.length === 1 ? scoped : [];
  if (matches.length) result[g.name] = {
    extensions: matches.flatMap(l => l.extensions ?? []), filenames: matches.flatMap(l => l.filenames ?? []),
    filenamePatterns: [], aliases: matches.flatMap(l => [l.name, ...(l.aliases ?? [])]),
    interpreters: matches.flatMap(l => l.interpreters ?? []), sources: [linguistSource],
  };
}
let next = 0;
await Promise.all(Array.from({ length: 8 }, async () => {
  while (next < grammars.length) {
    const g = grammars[next++];
    const match = g.source?.match(/^https:\/\/github.com\/([^/]+\/[^/]+)\/blob\/[^/]+\/(.+)$/);
    if (!match) continue;
    const dirs = match[2].split('/').slice(0, -1);
    // Most extensions use syntaxes/foo.json; monorepos can have a package farther up.
    const candidates = [...new Set([dirs.slice(0, -1).join('/'), '', ...dirs.map((_, i) => dirs.slice(0, dirs.length - i).join('/'))])];
    for (const dir of candidates) {
      const url = `https://raw.githubusercontent.com/${match[1]}/${g.sha}/${dir ? dir + '/' : ''}package.json`;
      const pkg = await manifest(url);
      const contribution = pkg?.contributes?.grammars?.find(grammar => grammar.scopeName === g.scopeName)
        ?? pkg?.contributes?.grammars?.find(grammar => grammar.path && match[2].toLowerCase().endsWith(grammar.path.replace(/^\.\//, '').toLowerCase()));
      const languages = pkg?.contributes?.languages ?? [];
      const language = languages.find(l => l.id === contribution?.language)
        ?? languages.find(l => l.id === g.name);
      if (!language) continue;
      const previous = result[g.name];
      // Publisher extension lists are authoritative. Linguist also lists ambiguous extensions
      // (for example XML translation files ending in .ts); use it only when the publisher omits them.
      result[g.name] = { extensions: language.extensions?.length ? language.extensions : previous?.extensions ?? [],
        filenames: [...new Set([...(previous?.filenames ?? []), ...(language.filenames ?? [])])],
        filenamePatterns: language.filenamePatterns ?? [], firstLine: language.firstLine ?? null,
        aliases: [...new Set([...(previous?.aliases ?? []), ...(language.aliases ?? []).filter(a => !a.startsWith('%'))])],
        interpreters: previous?.interpreters ?? [], sources: [...(previous?.sources ?? []), url] };
      break;
    }
  }
}));
await writeFile('scripts/textmate-associations.json', JSON.stringify(Object.fromEntries(Object.entries(result).sort()), null, 2) + '\n');
const license = await fetch(`https://raw.githubusercontent.com/github-linguist/linguist/${linguistRevision}/LICENSE`);
if (!license.ok) throw new Error('Could not read Linguist license');
await writeFile('src/Codale.Core/Syntax/Bundled/associations.NOTICE', `Language association metadata includes GitHub Linguist (${linguistRevision}).\nPublisher manifest and Linguist source URLs are recorded in catalog.json.\n\n${await license.text()}`);
console.log(`Captured association metadata for ${Object.keys(result).length}/${grammars.length} languages.`);
