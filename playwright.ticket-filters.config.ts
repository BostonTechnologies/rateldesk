import { defineConfig } from '@playwright/test';
import base from './playwright.config';
export default defineConfig(base, {
 testMatch:['**/ticket-filters.spec.ts'],testIgnore:[],retries:0,
 use:{...base.use,baseURL:'https://127.0.0.1:5200',ignoreHTTPSErrors:true},
 webServer:[
  {command:'node tests/ux/ticket-filter-fixture-api.mjs',url:'http://127.0.0.1:18330/fixture/health',reuseExistingServer:false},
  {command:process.env.HELPDESK_FILTERS_CONFIGURATION === 'Release' ? 'dotnet bin/Release/net10.0/HelpDesk.NewWeb.dll' : 'dotnet bin/Debug/net10.0/HelpDesk.NewWeb.dll',cwd:'src/HelpDesk.NewWeb',url:'https://127.0.0.1:5200/health',ignoreHTTPSErrors:true,timeout:120000,reuseExistingServer:false,
   env:{ASPNETCORE_ENVIRONMENT:'Development',ASPNETCORE_URLS:'https://127.0.0.1:5200',ApiBaseUrl:'http://127.0.0.1:18330/',DevelopmentOperator__Enabled:'true',SYSTEM_TOKEN_SECRET:'filter-admin',Authentication__Authentik__ClientId:'ux-local',AUTHENTIK_CLIENT_SECRET:'ux-local',Authentication__Authentik__ApiScope:'helpdesk'}}
 ]
});
