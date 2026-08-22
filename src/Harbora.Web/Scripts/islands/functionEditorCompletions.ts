// Completion content for the Functions code editor (`CodeEditor.vue`). Kept in its own module so
// the per-runtime word lists don't crowd the editor's own wiring.
//
// CodeMirror already ships real completion for two of the three runtimes, and it is not where the
// task's own brief expected it. `javascript()` and `python()` (both dynamically imported per
// runtime in `CodeEditor.vue`'s `loadLanguage`) register local-variable and global/keyword
// completion as language data the moment they load. And — checked directly against
// `node_modules/@codemirror/legacy-modes/mode/clike.js` rather than assumed — the "no first-class
// C# grammar" stream-mode parser is *not* silent either: its own `csharp` config already carries a
// flat `keywords`/`types`/`atoms` word list (`await`, `async`, `record`, `Task`, `Guid`, `Boolean`,
// …) as `languageData.autocomplete`, which `StreamLanguage.define()` forwards automatically the
// moment `autocompletion()` exists in the extension list — confirmed live, not just read off the
// source: typing near an empty line and firing completion surfaces exactly those words with zero
// wiring from this file. None of that is scope-aware or type-aware; it is `Object.keys(...)` on a
// hand-written table, same category of thing as everything below it.
//
// So this module adds only the residue nothing upstream already covers: the function's own shape
// (a handler skeleton, reading an env var, returning JSON — the exact idioms
// `Harbora.Infrastructure/Functions/FunctionStarters.cs` already pre-fills the editor with, never a
// second style) for all three runtimes, and, for C# only, the handful of this project's own
// contract type names (`FnRequest`, `FnResponse`, …) that obviously cannot appear in a generic C#
// word list because they don't exist outside this app.
import { completeAnyWord, snippetCompletion, type CompletionSource, type Completion } from '@codemirror/autocomplete';

/**
 * `Harbora.Functions.Contract.cs` (rewritten into every C# function build) is the fixed, small
 * surface a function body actually calls, and the one part of "C# completion" that
 * `@codemirror/legacy-modes`' generic word list could never contain — these types don't exist
 * anywhere outside a Harbora-generated host. `Console`, `Task`, `DateTimeOffset` and friends are
 * deliberately absent from this list: they're already in clike's own `types` table (see the module
 * comment above), and repeating them here would just show every one of them twice in the popup.
 */
const CSHARP_CONTRACT_TYPES = ['FnRequest', 'FnResponse', 'FnContext', 'FnEvent'];

/** The one addition C#'s completion still needs once the built-in word list is accounted for. */
export function csharpContractCompletions(): Completion[] {
  return CSHARP_CONTRACT_TYPES.map((label): Completion => ({ label, type: 'class' }));
}

/**
 * Plain `completeAnyWord` (whole-buffer word completion — the "less like Notepad" baseline every
 * runtime gets) would show every one of `csharpContractCompletions()`'s names a second time the
 * moment they're actually used: the C# "fn" snippet below inserts `FnRequest req, FnContext ctx`
 * and `FnResponse` as literal text, so from that point on the buffer itself contains those words
 * too. Filtering them back out of the word source — rather than dropping the word source, or
 * dropping the authoritative contract-type list that's useful *before* "fn" is ever accepted — is
 * the fix that keeps both without a function this app defines showing up twice in its own editor.
 */
export const csharpWordCompletion: CompletionSource = (context) => {
  const result = completeAnyWord(context);
  if (!result) return result;
  return { ...result, options: result.options.filter((o) => !CSHARP_CONTRACT_TYPES.includes(o.label)) };
};

/**
 * The function's own shape, one snippet per idiom `FunctionStarters.cs` already establishes for
 * this runtime: the exact handler signature the generated host calls, reading an env var off
 * `ctx`/`FnContext`, and returning JSON the way that runtime's starter already does. Typing the
 * label and accepting the completion is the whole interaction — Tab then walks the snippet's own
 * fields via CodeMirror's own snippet field navigation, not anything implemented here.
 */
export function functionSnippets(runtime: string, fa: boolean): Completion[] {
  const detail = (en: string, faText: string) => (fa ? faText : en);
  const fn = detail('Function handler skeleton', 'قالب پایه هندلر تابع');
  const env = detail('Read an environment variable', 'خواندن یک متغیر محیطی');
  const json = detail('Return a JSON response', 'بازگرداندن پاسخ JSON');

  switch (runtime) {
    case 'JavaScript':
      return [
        snippetCompletion('export default async function (req, ctx) {\n\t${}\n}',
          { label: 'fn', type: 'snippet', detail: fn }),
        snippetCompletion("const ${name} = ctx.env.${KEY} || '${fallback}';",
          { label: 'env', type: 'snippet', detail: env }),
        snippetCompletion('return { ${key}: ${value} };',
          { label: 'json', type: 'snippet', detail: json }),
      ];
    case 'Python':
      return [
        snippetCompletion('def run(req, ctx):\n\t${}',
          { label: 'fn', type: 'snippet', detail: fn }),
        snippetCompletion("${name} = ctx['env'].get('${KEY}', '${fallback}')",
          { label: 'env', type: 'snippet', detail: env }),
        snippetCompletion("return {'${key}': ${value}}",
          { label: 'json', type: 'snippet', detail: json }),
      ];
    case 'CSharp':
    default:
      return [
        snippetCompletion(
          'public static class Function\n{\n\tpublic static Task<FnResponse> Run(FnRequest req, FnContext ctx)\n\t{\n\t\t${}\n\t\treturn Task.FromResult(FnResponse.Empty());\n\t}\n}',
          { label: 'fn', type: 'snippet', detail: fn }),
        snippetCompletion('var ${name} = ctx.Env.TryGetValue("${KEY}", out var value) ? value : "${fallback}";',
          { label: 'env', type: 'snippet', detail: env }),
        snippetCompletion('return Task.FromResult(FnResponse.Json(new { ${key} = ${value} }));',
          { label: 'json', type: 'snippet', detail: json }),
      ];
  }
}
