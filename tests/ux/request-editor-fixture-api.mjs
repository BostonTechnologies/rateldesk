// Loopback-only synthetic editor data; never dispatches external automation.
import http from 'node:http';
const connectionId = '11111111-1111-1111-1111-111111111111';
const taskIds = ['aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'cccccccc-cccc-cccc-cccc-cccccccccccc'];
const organizations = [{id:'org-1',name:'Example organization'}];
const inputs = Array.from({length:14},(_,i)=>({key:`input_${i+1}`,label:`Synthetic input ${i+1}`,type:'text',order:i+1}));
const nodeIds = Array.from({length:5},(_,i)=>`eeeeeeee-eeee-eeee-eeee-${String(i+1).padStart(12,'0')}`);
const groupId='ffffffff-ffff-ffff-ffff-ffffffffffff';
const targetChoices=[...nodeIds.map((id,i)=>({id,name:`Synthetic worker ${i+1}`,kind:'explicitNodes',target:{version:1,source:2,agentIds:[id]}})),
 {id:groupId,name:'Synthetic HA pool',kind:'executionGroup',target:{version:1,source:1,executionGroupId:groupId}}];
let form, bindings, saves, attempts, fault, catalogue, creations;
const clone = value=>JSON.parse(JSON.stringify(value));
function reset(mode='') {
 fault='';saves=[];attempts=[];creations=[];
 catalogue=[{requestDefinitionId:'definition-1',requestDefinitionName:'Synthetic read-only target',tenantId:17,clientIdentity:'synthetic-client',inputs}];
 if(mode.startsWith('modern')) catalogue=mode==='modern-empty'?[]:[
  {id:'9007199254740993',name:'Synthetic HA job',kind:'executionGroup',summary:'HA group: Synthetic HA pool (one eligible worker)',definition:{version:1,source:1,executionGroupId:groupId}},
  {id:'9007199254740995',name:'Synthetic five-node job',kind:'explicitNodes',summary:'5 selected nodes',definition:{version:1,source:2,agentIds:nodeIds}}
 ].map(job=>({contractVersion:2,requestDefinitionId:job.id,orchestrationJobDefinitionId:job.id,requestDefinitionName:job.name,orchestrationJobDefinitionName:job.name,displayName:job.name,folderPath:'/synthetic/',tenantId:17,clientIdentity:'',inputs:[],expectedRuntimeSeconds:60,target:{version:1,kind:job.kind,summary:job.summary,definition:job.definition,readiness:'worker-check-at-run',requiredInputs:[]}}));
 const fields=[{key:'request_reason',label:'Request reason',type:'text',required:true},{key:'device_type',label:'Device type',type:'predefined',predefinedOptions:['Notebook','Desktop'],required:false}];
 const tasks=[{id:taskIds[0],name:'Prepare access — synthetic automation',description:'Review and map a harmless synthetic request.',type:'automation',order:1,autoStart:true,payloadMapping:Object.fromEntries(inputs.map(x=>[x.key,'request_reason'])),dependsOn:[]},
 {id:taskIds[1],name:'Review result',type:'manual',order:2,autoStart:true,dependsOn:[taskIds[0]],description:'Human review retained while reordering.'},
 {id:taskIds[2],name:'Approve completion',type:'approval',order:3,autoStart:true,dependsOn:[taskIds[1]],approvalAllowedDays:7,approvalApprovers:[{source:'User',id:'operator',name:'Synthetic Operator',email:'operator@example.test'}]}];
 if(mode==='invalid-approval')tasks[2].approvalApprovers=[];
 if(mode==='cycle')tasks[0].dependsOn=[taskIds[1]];
 if(mode==='negative-sla')tasks[1].taskSlaMinutes=-1;
 if(mode==='empty')tasks.splice(0);
 if(mode==='many')tasks.push(...Array.from({length:30},(_,i)=>({id:`dddddddd-dddd-dddd-dddd-${String(i+1).padStart(12,'0')}`,name:`Synthetic long workflow task ${i+1} with enough words to wrap correctly`,type:'manual',order:i+4,dependsOn:[]})));
 form={id:'form-1',serviceId:'service-1',title:'Example access request',description:'Synthetic request editor acceptance',organizationId:'org-1',allowedOrganizationIds:['org-1'],releaseStatus:0,jsonSchema:JSON.stringify({fields,tasks})};
 bindings=mode==='empty'?[]:[{id:'binding-1',requestFormId:'form-1',taskTemplateId:taskIds[0],organizationId:'org-1',systemConnectionId:connectionId,orchestrationRequestDefinitionId:'definition-1',orchestrationRequestDefinitionName:'Synthetic read-only target',enabled:true,syncState:0}];
}
reset();
http.createServer(async(req,res)=>{
 const url=new URL(req.url,'http://127.0.0.1');const path=url.pathname.replace(/\/$/,'');
 const reply=(value,status=200)=>{res.writeHead(status,{'Content-Type':'application/json'});res.end(JSON.stringify(value));};
 const body=async()=>{let value='';for await(const chunk of req)value+=chunk;return value?JSON.parse(value):{};};
 if(path==='/fixture/health')return reply({ok:true});
 if(path==='/fixture/reset'){reset(url.searchParams.get('mode')??'');return reply({ok:true});}
 if(path==='/fixture/fault'){fault=url.searchParams.get('mode');return reply({ok:true});}
 if(path==='/fixture/state')return reply({form,bindings,saves,attempts,creations});
 if(path.endsWith('/system/token')){const encode=value=>Buffer.from(JSON.stringify(value)).toString('base64url');return reply({token:`${encode({alg:'none'})}.${encode({sub:'synthetic-editor',auth_mode:'development',preferred_username:'Synthetic Administrator',email:'admin@example.test',organization_id:'org-1',roles:['HelpdeskAdmin'],allowed_organization_id:'org-1',managed_organization_id:'org-1',exp:Math.floor(Date.now()/1000)+3600})}.fixture`});}
 if(path==='/api/v1/auth/me')return reply({isAuthenticated:true,name:'Synthetic Administrator',email:'admin@example.test',userId:'synthetic-editor',customerId:null,primaryOrganizationId:'org-1',isHelpdeskAdmin:true,roles:['HelpdeskAdmin'],roleBundles:['HelpdeskAdmin'],permissions:['HelpdeskAdmin'],allowedOrganizationIds:['org-1'],managedOrganizationIds:['org-1']});
 if(path==='/api/v1/branding')return reply({applicationName:'RatelDesk',faviconUrl:'/favicon.ico'});
 if(path==='/api/v1/organizations')return reply(organizations);
 if(path==='/api/v1/users')return reply([{id:'operator',name:'Synthetic Operator',email:'operator@example.test',organizationId:'org-1'}]);
 if(path==='/api/v1/services/service-1')return reply({id:'service-1',name:'Synthetic service',depth:1});
 if(path==='/api/v1/services/service-1/breadcrumb')return reply([{id:'service-1',name:'Synthetic service'}]);
 if(path==='/api/v1/service-items/service-1')return reply([{id:form.id,name:form.title,itemType:1,description:form.description,releaseStatus:form.releaseStatus,allowedOrganizationIds:['org-1']}]);
 if(path==='/api/v1/admin/system-connections')return reply([{id:connectionId,pairId:'synthetic-pair',mapping:{id:connectionId,pairId:'synthetic-pair',name:'Synthetic automation',netRatelTenantId:'17',ratelDeskOrganizationId:'org-1',ingestIncidents:false,runAutomation:true},peer:{contract:'bostec.pairing.v1',product:'netratel',installationId:'synthetic-peer',name:'Synthetic peer',webOrigin:'https://peer.example.test',apiOrigin:'https://peer-api.example.test'},status:'Connected'}]);
 if(path==='/api/v1/admin/orchestration')return reply({enabled:true,source:'pairing',sourceKey:connectionId,localTenantId:'org-1',peerTenantId:'17'});
 if(path.endsWith('/catalog/capabilities'))return reply({contractVersion:2,catalogue:true,authoring:true,aggregateExecution:true,targetKinds:['legacyAgent','explicitNodes','executionGroup','nodeFilter','runtimeInput']});
 if(path.endsWith('/catalog/target-choices'))return reply(targetChoices);
 if(path.endsWith('/catalog/request-definitions')&&req.method==='GET')return reply(catalogue);
 if(path.endsWith('/catalog/request-definitions')&&req.method==='POST'){
  const payload=await body();creations.push({payload,organizationId:url.searchParams.get('organizationId'),connectionId:url.searchParams.get('connectionId')});
  if(fault==='job-create'){fault='';return reply({operation:'create-job',status:422,code:'target_unavailable',message:'Synthetic target validation failed (target_unavailable). Reference: fixture-create-reference.',correlationId:'fixture-create-reference'},422);}
  const created={contractVersion:2,requestDefinitionId:'9007199254740997',orchestrationJobDefinitionId:'9007199254740997',requestDefinitionName:payload.name,orchestrationJobDefinitionName:payload.name,displayName:payload.name,folderPath:payload.folderPath,tenantId:17,clientIdentity:'',inputs:payload.inputs,expectedRuntimeSeconds:60,target:{version:1,kind:'explicitNodes',summary:'1 selected node',definition:payload.target,readiness:'worker-check-at-run',requiredInputs:[]}};
  catalogue.push(created);return reply(created,201);
 }
 if(path.endsWith('/catalog/jobs'))return reply([]);
 if(path.endsWith('/drift-preview'))return reply({bindingId:'binding-1',requestFormId:'form-1',taskTemplateId:taskIds[0],syncState:0,helpdeskInputs:[],orchestrationInputs:[],differences:[]});
 if(path==='/api/v1/admin/orchestration/bindings'&&req.method==='GET')return reply(bindings);
 if(path==='/api/v1/request-forms/form-1'&&req.method==='GET')return reply(form);
 if(path==='/api/v1/admin/orchestration/bindings/refresh-drift')return reply(bindings);
 if(path.endsWith('/sync-inputs'))return reply(bindings.find(x=>path.includes(x.id))??{});
 if(req.method!=='GET'&&path.startsWith('/api/')){
  const payload=await body();attempts.push({path,method:req.method});
  if(path.includes('/bindings/')&&req.method==='PUT'&&fault==='binding-save'){fault='';return reply({message:'Synthetic binding sync failure'},400);}
  saves.push({path,method:req.method,payload:clone(payload)});
  if(path==='/api/v1/request-forms'&&req.method==='POST'){form={...payload,id:'form-1'};return reply(form);}
  if(path==='/api/v1/request-forms/form-1'&&req.method==='PUT'){form={...form,...payload};return reply(form);}
  if(path==='/api/v1/admin/orchestration/bindings'&&req.method==='POST'){const binding={...payload,id:`binding-${bindings.length+1}`,organizationId:'org-1',enabled:true,syncState:0};bindings.push(binding);return reply(binding);}
  if(path.startsWith('/api/v1/admin/orchestration/bindings/')&&req.method==='PUT'){const binding=bindings.find(x=>path.endsWith(x.id));Object.assign(binding,payload);return reply(binding);}
  if(path.startsWith('/api/v1/admin/orchestration/bindings/')&&req.method==='DELETE'){bindings=bindings.filter(x=>!path.endsWith(x.id));return reply({});}
  return reply({});
 }
 if(['/api/v1/incidents','/api/v1/requests','/api/v1/changes'].includes(path))return reply({items:[],total:0,totalCount:0});
 if(path.endsWith('/count'))return reply(0);
 if(path.includes('summary')||path.includes('status'))return reply({});
 if(path.includes('/stream')){res.writeHead(200,{'Content-Type':'text/event-stream'});return res.end();}
 return reply([]);
}).listen(18530,'127.0.0.1',()=>console.log('Synthetic request editor fixture :18530'));
