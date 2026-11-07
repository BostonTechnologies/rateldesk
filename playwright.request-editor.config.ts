import { defineConfig } from '@playwright/test';
const cwd=process.env.HELPDESK_EDITOR_WEB_CWD??'src/HelpDesk.NewWeb';
const configuration=process.env.HELPDESK_EDITOR_CONFIGURATION??'Debug';
export default defineConfig({
 testDir:'./tests/ux',testMatch:'**/request-editor.spec.ts',workers:1,fullyParallel:false,retries:0,timeout:60_000,
 outputDir:process.env.HELPDESK_EDITOR_OUTPUT??'test-results/request-editor',
 reporter:[['list'],['json',{outputFile:process.env.HELPDESK_EDITOR_REPORT??'test-results/request-editor-results.json'}]],
 use:{baseURL:'https://127.0.0.1:5190',ignoreHTTPSErrors:true,viewport:{width:1440,height:900},screenshot:'only-on-failure',trace:'off',launchOptions:process.env.HELPDESK_E2E_CHROMIUM_PATH?{executablePath:process.env.HELPDESK_E2E_CHROMIUM_PATH}:undefined},
 webServer:[
  {command:'node tests/ux/request-editor-fixture-api.mjs',url:'http://127.0.0.1:18530/fixture/health',reuseExistingServer:false},
  {command:`dotnet bin/${configuration}/net10.0/HelpDesk.NewWeb.dll`,cwd,url:'https://127.0.0.1:5190/health',ignoreHTTPSErrors:true,timeout:120_000,reuseExistingServer:false,stdout:'pipe',stderr:'pipe',
   env:{ASPNETCORE_ENVIRONMENT:'Development',ASPNETCORE_URLS:'https://127.0.0.1:5190',ApiBaseUrl:'http://127.0.0.1:18530/',DevelopmentOperator__Enabled:'true',SYSTEM_TOKEN_SECRET:'editor-fixture',Authentication__Authentik__ClientId:'ux-local',AUTHENTIK_CLIENT_SECRET:'ux-local',Authentication__Authentik__ApiScope:'helpdesk'}}
 ]
});
