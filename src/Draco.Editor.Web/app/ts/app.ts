import * as monaco from 'monaco-editor/esm/vs/editor/editor.main.js'; // Monaco is VSCode core, but limited due to browser environement.
import onigasmWasm from 'onigasm/lib/onigasm.wasm'; // TextMates regex parser lib compiled in WASM.
import { loadWASM } from 'onigasm'; // Helper shipped with it to load it.
import { Registry } from 'monaco-textmate';
import { wireTmGrammars } from 'monaco-editor-textmate'; // Library that allow running Textmates grammar in monaco.
import grammarDefinition from '../../../Draco.SyntaxHighlighting/draco.tmLanguage.json';
import { deflateRaw, inflateRaw } from 'pako';

// This file is run on page load.
// This run before blazor load, and will tell blazor to start.

const worker = new Worker('worker.js'); // first thing: we start the worker so it load in parallel.


self.MonacoEnvironment = {
    // Web Workers need to start a new script, by url.
    // This is the path where the script of the webworker is served.
    getWorkerUrl: function () {
        return './editor.worker.js';
    }
};

function isDarkMode() {
    // From: https://stackoverflow.com/questions/56393880/how-do-i-detect-dark-mode-using-javascript
    return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
}

function fromBase64ToBase64URL(str: string) {
    return str
        .replace('+', '-')
        .replace('/', '_');
}

function fromBase64URLToBase64(str: string) {
    return str
        .replace('_', '/')
        .replace('-', '+');
}

function toBase64(u8) {
    return btoa(String.fromCharCode.apply(null, u8));
}

function fromBase64(str) {
    return new Uint8Array(atob(str).split('').map(c => c.charCodeAt(0)));
}

function updateHash() {
    const source = dracoEditor.getModel().createSnapshot().read();
    // setting the URL Hash with the state of the editor.
    // Doing this before invoking DotNet will allow sharing hard crash.
    const content = outputTypeSelector.value + '\n' + source;
    const encoded = new TextEncoder().encode(content);
    const compressed = deflateRaw(encoded);
    const buffer = new Uint8Array(compressed.length + 1);
    buffer[0] = 1; // version, for future use.
    buffer.set(compressed, 1);
    history.replaceState(undefined, undefined, '#'+fromBase64ToBase64URL(toBase64(buffer)) );
}

// We export this method so the C# runtime can call them.

/**
 * Sets the text of the output monaco-editor.
 * @param text
 */
export function setOutputText(text: string) {
    outputEditor.getModel().setValue(text);
}

// this is the element that allow to select the output type.
const outputTypeSelector = document.getElementById('output-type-selector') as HTMLSelectElement;

const hash = window.location.hash.slice(1);
let inputCode = `func main() {
    println("Hello!");
}
`;

if (hash != null && hash.trim().length > 0) {
    // We store data in the hash of the url, so we need to decode it on load.
    try {
        const b64 = fromBase64URLToBase64(hash);// our hash is encoded in base64 url: https://en.wikipedia.org/wiki/Base64#URL_applications
        let buffer = fromBase64(b64);
        buffer = buffer.subarray(1); // Version byte, for future usage.
        const uncompressed = inflateRaw(buffer);
        const str = new TextDecoder().decode(uncompressed);
        const firstNewLine = str.indexOf('\n');
        outputTypeSelector.value = str.slice(0, firstNewLine);
        inputCode = str.slice(firstNewLine + 1);
    } catch (e) {
        inputCode = `Error while decoding the URL hash. ${e}`;
    }
}

outputTypeSelector.onchange = () => {
    updateHash();
    const newVal = outputTypeSelector.value;
    switch (newVal) {
    case 'CSharp':
        monaco.editor.setModelLanguage(outputEditor.getModel(), 'csharp');
        break;
    case 'IL':
        monaco.editor.setModelLanguage(outputEditor.getModel(), 'il');
        break;
    default:
        monaco.editor.setModelLanguage(outputEditor.getModel(), 'none');
        break;
    }
    // We relay the output type change to C#.
    worker.postMessage({
        type: 'OnOutputTypeChange',
        paylod: newVal
    });
};

const dracoEditor = monaco.editor.create(document.getElementById('draco-editor'), {
    value: inputCode,
    language: 'draco',
    theme: 'dynamic-theme'
});

dracoEditor.onDidChangeModelContent(() => {
    updateHash();
    worker.postMessage({
        type: 'CodeChange',
        payload: dracoEditor.getModel().createSnapshot().read()
    });
});

const outputEditor = monaco.editor.create(document.getElementById('output-viewer'), {
    value: ['.NET Runtime loading...'].join('\n'),
    theme: 'dynamic-theme',
    readOnly: true
});

async function main() {
    const cfg = await (await fetch('_framework/blazor.boot.json')).json();
    console.log(cfg);
    worker.postMessage(cfg);
    worker.postMessage({
        type: 'OnInit',
        payload: {
            OutputType: outputTypeSelector.value,
            Code: dracoEditor.getModel().createSnapshot().read()
        }
    });
    const wasmPromise = loadWASM(onigasmWasm.buffer); // https://www.npmjs.com/package/onigasm;

    const choosenTheme = window.localStorage.getItem('theme'); // get previous user theme choice
    const themes = await (await fetch('themes.json')).json();
    function setTheme(theme: string) {
        try {
            if (theme == 'Default' || theme == null) {
                window.localStorage.removeItem('theme');
            } else {
                window.localStorage.setItem('theme', theme);
            }
        } catch (e) {
            console.error(e);
        }
        let currentTheme = theme;
        if (themes[theme] == undefined) {
            currentTheme = isDarkMode() ? 'Dark+ (default dark)' : 'Light+ (default light)';
        }
        let selectedTheme = themes[currentTheme];
        if (selectedTheme == undefined) {
            selectedTheme = Object.values(selectedTheme)[0]; // defensive programming: dark_vs, and light_vs don't exists anymore.
        }
        monaco.editor.defineTheme('dynamic-theme', selectedTheme as monaco.editor.IStandaloneThemeData);
        monaco.editor.setTheme('dynamic-theme');
    }
    setTheme(choosenTheme);


    const themeSelector = document.getElementById('theme-selector') as HTMLSelectElement;
    const defaultOption = document.createElement('option');


    defaultOption.innerText = defaultOption.value = 'Default';
    themeSelector.appendChild(defaultOption);
    Object.keys(themes).forEach(s => {
        const option = document.createElement('option');
        option.innerText = option.value = s;
        themeSelector.appendChild(option);
    });
    themeSelector.value = choosenTheme ?? 'Default';
    themeSelector.onchange = () => {
        setTheme(themeSelector.value);
    };
    await wasmPromise;

    const registry = new Registry({
        getGrammarDefinition: async (scopeName) => {
            switch (scopeName) {
            case 'source.draco':
                return {
                    format: 'json',
                    content: grammarDefinition
                };
            case 'source.cs':
                return {
                    format: 'json',
                    content: await (await fetch('csharp.tmLanguage.json')).text()
                };
            case 'source.il':
                return {
                    format: 'json',
                    content: await (await fetch('il.tmLanguage.json')).text()
                };
            default:
                return null;
            }

        }
    });

    // map of monaco "language id's" to TextMate scopeNames
    const grammars = new Map([
        ['draco', 'source.draco'],
        ['csharp', 'source.cs'],
        ['il', 'source.il']
    ]);
    for (const language of grammars.keys()) {
        monaco.languages.register({ id: language });

    }
    await wireTmGrammars(monaco, registry, grammars);
}
main();
