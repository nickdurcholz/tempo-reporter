global using CliFx;
global using CliFx.Attributes;
global using CliFx.Exceptions;

return await new CliApplicationBuilder().AddCommandsFromThisAssembly().Build().RunAsync();