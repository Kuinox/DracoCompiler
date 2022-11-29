// ts/worker.ts
importScripts("./dotnet.js");
var firstMessageResolve;
var firstMessagePromise = new Promise(
  (resolve) => {
    firstMessageResolve = resolve;
  }
);
onmessage = (e) => {
  if (firstMessageResolve != void 0)
    firstMessageResolve(e.data);
  firstMessageResolve = void 0;
};
async function main() {
  console.log("Worker starting...");
  const monoCfg = await firstMessagePromise;
  firstMessagePromise = void 0;
  const dotnet = self.dotnet.dotnet;
  dotnet.moduleConfig.configSrc = null;
  const { setModuleImports, getAssemblyExports } = await dotnet.withDiagnosticTracing(true).withConfig(monoCfg).create();
  await dotnet.run();
}
main();
