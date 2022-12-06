importScripts('./dotnet.js');
declare global { // Blazor do not provide types, so we have our own to please typescript.
    interface Window {
        dotnet: any;
    }
}


let firstMessageResolve;
let firstMessagePromise = new Promise(
    resolve => firstMessageResolve = resolve
);

let initResolve;
let initPromise = new Promise(
    resolve => initResolve = resolve
);

let csOnMessage;

onmessage = async (e: MessageEvent<unknown>) => {
    if (firstMessageResolve != undefined) {
        firstMessageResolve(e.data);
        firstMessageResolve = undefined;
        return;
    }
    if (initPromise != undefined) {
        await initPromise;
        initPromise = undefined;
        initResolve = undefined;
    }
    console.log(e.data);
    try {

        csOnMessage(e.data['type'], JSON.stringify(e.data['payload']));
    } catch (e) {
        console.log(e);
        throw e;
    }

};

function sendMessage(type: string, message: string) {
    console.log(type);
    console.log(message);
    postMessage({
        type: type,
        message: message
    });
}

async function main() {
    console.log('Worker starting...');
    const monoCfg = await firstMessagePromise;
    console.log('Received boot config.');
    firstMessagePromise = undefined;
    const dotnet = self.dotnet.dotnet;
    dotnet.moduleConfig.configSrc = null;
<<<<<<< HEAD
    const dlls = [
        {
            'behavior': 'dotnetwasm',
            'name': 'dotnet.wasm'
        }
    ];

    function addDll(name) {
        dlls.push({
            'behavior': 'assembly',
            'name': name
        });
    }
    const mainAssemblyName = 'Draco.Editor.Web';
    [
        'System.Runtime.InteropServices.JavaScript.dll',
        'System.Collections.dll',
        'System.Memory.dll',
        'System.Runtime.dll',
        'System.Runtime.InteropServices.dll',
        'System.Threading.dll',
        'System.Console.dll',
        'netstandard.dll',
        'System.Private.CoreLib.dll',
        'Microsoft.AspNetCore.Components.WebAssembly.dll',
        'System.Text.Json.dll',
        'System.ComponentModel.dll',
        'Microsoft.Extensions.Configuration.Abstractions.dll',
        'Microsoft.AspNetCore.Components.Web.dll',
        'Microsoft.Extensions.DependencyInjection.Abstractions.dll',
        'Microsoft.Extensions.Logging.dll',
        'Microsoft.JSInterop.WebAssembly.dll',
        'Microsoft.JSInterop.dll',
        'System.Collections.Concurrent.dll',
        'Microsoft.AspNetCore.Components.dll',
        'System.Text.Encodings.Web.dll',
        'System.Text.Encoding.Extensions.dll',
        'System.Numerics.Vectors.dll',
        'System.Runtime.Intrinsics.dll',
        'System.Private.Uri.dll',
        'Microsoft.CodeAnalysis.CSharp.dll',
        'Microsoft.CodeAnalysis.dll',
        mainAssemblyName + '.dll',
    ].forEach(s => addDll(s));

=======
>>>>>>> e74f841 (mhhh)
    const { setModuleImports, getAssemblyExports, } = await dotnet
        .withDiagnosticTracing(true)
        .withConfig(
            monoCfg
<<<<<<< HEAD
            // {
            // mainAssemblyName: mainAssemblyName,
            // assemblyRootFolder: '_framework',
            // assets: dlls
            // }
        )
        .create();
    setModuleImports(
        'sendMessage',
        sendMessage
    );
    const exports = await getAssemblyExports(mainAssemblyName);
=======
        )
        .create();
    setModuleImports(
        'worker.js',
        {
            Interop: {
                sendMessage
            }
        }
    );
    const exports = await getAssemblyExports(monoCfg['mainAssemblyName']);
>>>>>>> e74f841 (mhhh)
    csOnMessage = exports.Draco.Editor.Web.Interop.OnMessage;
    initResolve();
    await dotnet.run();
}
main();
