import { expect, test, Page } from '@playwright/test';
const fixture='http://127.0.0.1:18530/fixture';
const baseline=process.env.HELPDESK_EDITOR_BASELINE==='true';
async function openEditor(page:Page,expectTasks=true){
 await page.goto('/admin/requests/services/service-1',{waitUntil:'domcontentloaded'});
 await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive','true',{timeout:20_000});
 await expect(page.locator('.svc-card')).toContainText('Example access request');
 await page.locator('.svc-card .svc-actions button').first().click();
 await expect(page.locator('.request-form-builder-dialog')).toBeVisible({timeout:20_000});
 if(!baseline){await expect(page.getByRole('tab',{name:'Form setup',exact:true})).toHaveAttribute('aria-selected','true');await expect(page.locator('.request-form-tabs [role=tab]')).toHaveText(['Form setup',/^Form fields \(/,/^Workflow \(/]);await page.getByRole('tab',{name:/^Workflow \(/}).click();}
 if(!baseline&&expectTasks&&(page.viewportSize()?.width??1440)<=760)await page.locator('[data-task-id]').first().click();
 if(expectTasks)await expect(page.getByRole('textbox',{name:/^Task Name\*?$/})).toHaveValue('Prepare access — synthetic automation');
 await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
}
async function settled(page:Page){await page.evaluate(async()=>{await document.fonts.ready;await new Promise<void>(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve())));});}
test.beforeEach(async({page,request})=>{page.on('requestfailed',request=>console.log('EDITOR REQUEST FAILED',request.url(),request.failure()?.errorText));page.on('framenavigated',frame=>{if(frame===page.mainFrame())console.log('EDITOR NAV',frame.url());});page.on('pageerror',error=>console.log('EDITOR PAGE ERROR',error.message));page.on('console',message=>{if(message.type()==='error')console.log('EDITOR CONSOLE',message.text());});await request.get(`${fixture}/reset`);await page.goto('/auth/development',{waitUntil:'domcontentloaded'});});
test('synthetic before and after geometry evidence',async({page},info)=>{
 test.setTimeout(120_000);
 for(const [width,height] of [[1440,900],[1366,768],[1280,720],[390,844]]){
  await page.setViewportSize({width,height});await openEditor(page);await page.getByRole('tab',{name:'Form setup',exact:true}).click();await settled(page);await page.screenshot({path:info.outputPath(`setup-${width}x${height}.png`)});await page.getByRole('tab',{name:/^Workflow \(/}).click();if(width<=760&&await page.locator('.request-form-task-list-panel').isVisible())await page.locator('[data-task-id]').first().click();await settled(page);
  if(!baseline){
   await expect(page.getByRole('tab',{name:'Workflow (3)'})).toHaveAttribute('aria-selected','true');
   await expect(page.getByTestId('task-automation')).toBeVisible();
   const geometry=await page.locator('.request-form-builder-dialog').evaluate(el=>{
    const dialog=el.getBoundingClientRect();const nav=el.querySelector('.request-form-task-list-panel')!.getBoundingClientRect();const editor=el.querySelector('.request-form-task-config-panel')!.getBoundingClientRect();const save=[...el.querySelectorAll('button')].find(x=>x.textContent?.trim()==='Save')!.getBoundingClientRect();
    return{dialog:{top:dialog.top,bottom:dialog.bottom,width:dialog.width},nav:{width:nav.width,right:nav.right},editor:{left:editor.left,top:editor.top,width:editor.width},save:{top:save.top,bottom:save.bottom},scroll:el.scrollWidth,client:el.clientWidth};});
   await info.attach(`geometry-${width}x${height}`,{body:JSON.stringify(geometry,null,2),contentType:'application/json'});
   expect(geometry.dialog.top).toBeGreaterThanOrEqual(-1);expect(geometry.dialog.bottom).toBeLessThanOrEqual(height+1);expect(geometry.save.bottom).toBeLessThanOrEqual(height);
   expect(geometry.scroll).toBeLessThanOrEqual(geometry.client+2);
   if(width>760){expect(geometry.nav.width).toBeGreaterThanOrEqual(280);expect(geometry.nav.width).toBeLessThanOrEqual(340);expect(geometry.editor.left).toBeGreaterThan(geometry.nav.right);expect(geometry.editor.width).toBeGreaterThan(600);}
   else {await expect(page.getByRole('button',{name:'Back to tasks'})).toBeVisible();await page.getByRole('button',{name:'Back to tasks'}).click();await expect(page.locator('.request-form-task-list-panel')).toBeVisible();await expect(page.getByTestId('selected-task-editor')).toBeHidden();await page.locator('[data-task-id]').nth(1).click();await expect(page.getByRole('textbox',{name:/^Task Name\*?$/})).toHaveValue('Review result');}
  }
  await page.screenshot({path:info.outputPath(`${baseline?'before':'after'}-${width}x${height}.png`)});
  await page.getByRole('button',{name:'Cancel',exact:true}).click();
 }
});
if(!baseline){
 test('task selection, tab drafts, keyboard order and save/reload retain stable identities',async({page,request})=>{
  await openEditor(page);
  await page.getByRole('tab',{name:'Workflow (3)'}).focus();await page.keyboard.press('ArrowLeft');await expect(page.getByRole('tab',{name:'Form fields (2)'})).toBeFocused();await expect(page.getByRole('tab',{name:'Form fields (2)'})).toHaveAttribute('aria-selected','true');await page.keyboard.press('ArrowRight');await expect(page.getByRole('tab',{name:'Workflow (3)'})).toBeFocused();
  await page.locator('[data-task-id]').nth(1).click();
  const name=page.getByRole('textbox',{name:/^Task Name\*?$/});
  await expect(name).toHaveValue('Review result');await name.fill('Edited review');await name.blur();
  await page.getByRole('button',{name:'Move task up',exact:true}).focus();await page.keyboard.press('Enter');
  await expect(page.locator('[data-task-id]').first()).toHaveAttribute('data-task-id','bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb');
  await expect(name).toHaveValue('Edited review');
  await page.getByRole('tab',{name:'Form fields (2)'}).click();
  await page.getByRole('textbox',{name:/^Field label\*?$/}).first().fill('Edited reason');
  await page.getByRole('tab',{name:'Form setup',exact:true}).click();
  await page.getByRole('textbox',{name:'Description',exact:true}).fill('Changed form setup');
  await page.getByRole('tab',{name:'Workflow (3)'}).click();await expect(name).toHaveValue('Edited review');
  await page.getByRole('button',{name:'Save',exact:true}).click();await expect(page.locator('.request-form-builder-dialog')).toBeHidden();
  const state=await(await request.get(`${fixture}/state`)).json();const schema=JSON.parse(state.form.jsonSchema);
  expect(schema.tasks.map((x:any)=>x.id)).toEqual(['bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa','cccccccc-cccc-cccc-cccc-cccccccccccc']);
  expect(schema.tasks[0].dependsOn).toEqual(['aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa']);expect(schema.tasks[0].name).toBe('Edited review');
  expect(schema.fields[0]).toMatchObject({key:'request_reason',label:'Edited reason'});expect(schema.fields[1].predefinedOptions).toEqual(['Notebook','Desktop']);
  expect(state.form.description).toBe('Changed form setup');
  await page.locator('.svc-card .svc-actions button').first().click();await expect(page.getByRole('tab',{name:'Form setup',exact:true})).toHaveAttribute('aria-selected','true');await page.getByRole('tab',{name:'Workflow (3)'}).click();await expect(page.getByRole('textbox',{name:/^Task Name\*?$/})).toHaveValue('Edited review');
 });
 test('whole-draft validation activates hidden setup and task errors, and cancel discards',async({page,request})=>{
  await openEditor(page);
  await page.getByRole('tab',{name:'Form setup',exact:true}).click();await page.getByRole('textbox',{name:/^Form Title\*?$/}).fill('');
  await page.getByRole('tab',{name:'Workflow (3)'}).click();await page.getByRole('button',{name:'Save',exact:true}).click();
  await expect(page.getByRole('tab',{name:'Form setup',exact:true})).toHaveAttribute('aria-selected','true');await expect(page.getByRole('textbox',{name:/^Form Title\*?$/})).toBeFocused();
  await page.getByRole('textbox',{name:/^Form Title\*?$/}).fill('Example access request');
  await page.getByRole('tab',{name:'Workflow (3)'}).click();
  const secondTask=page.locator('[data-task-id]').nth(1);
  await secondTask.click();await expect(secondTask).toHaveAttribute('aria-pressed','true');
  await expect(page.getByRole('textbox',{name:/^Task Name\*?$/})).toHaveValue('Review result');
  await page.getByRole('textbox',{name:/^Task Name\*?$/}).fill('');
  await expect(secondTask).toContainText('Unnamed task');
  await page.locator('[data-task-id]').first().click();
  await expect(page.locator('[data-task-id]').first()).toHaveAttribute('aria-pressed','true');
  await page.getByRole('tab',{name:'Form fields (2)'}).click();await page.getByRole('button',{name:'Save',exact:true}).click();
  await expect(page.getByRole('tab',{name:'Workflow (3)'})).toHaveAttribute('aria-selected','true');await expect(page.getByRole('textbox',{name:/^Task Name\*?$/})).toHaveValue('');await expect(page.getByRole('textbox',{name:/^Task Name\*?$/})).toBeFocused();
  await expect(page.locator('.request-form-task-editor-content [role=alert]')).toContainText('Every task must have a name');
  expect((await(await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);
  await page.getByRole('button',{name:'Cancel',exact:true}).click();await expect(page.getByRole('dialog').last()).toContainText('Discard request form changes?');await page.getByRole('button',{name:'Keep editing',exact:true}).click();
  await expect(page.locator('.request-form-builder-dialog')).toBeVisible();await page.getByRole('button',{name:'Cancel',exact:true}).click();await page.getByRole('button',{name:'Discard',exact:true}).click();await expect(page.locator('.request-form-builder-dialog')).toBeHidden();
  const state=await(await request.get(`${fixture}/state`)).json();expect(JSON.parse(state.form.jsonSchema).tasks[1].name).toBe('Review result');expect(state.saves).toHaveLength(0);
 });
 test('a binding failure keeps the draft and retry updates the same saved form',async({page,request})=>{
  await openEditor(page);await request.get(`${fixture}/fault?mode=binding-save`);
  await page.getByRole('textbox',{name:/^Task Name\*?$/}).fill('Edited automation');await page.getByRole('button',{name:'Save',exact:true}).click();
  await expect(page.locator('.request-form-save-state')).toContainText('form was saved, but automation bindings were not fully saved');
  await expect(page.getByRole('textbox',{name:/^Task Name\*?$/})).toHaveValue('Edited automation');
  await page.getByRole('button',{name:'Save',exact:true}).click();await expect(page.locator('.request-form-builder-dialog')).toBeHidden();
  const state=await(await request.get(`${fixture}/state`)).json();expect(state.saves.filter((x:any)=>x.path==='/api/v1/request-forms/form-1'&&x.method==='PUT')).toHaveLength(2);expect(state.saves.filter((x:any)=>x.path==='/api/v1/request-forms'&&x.method==='POST')).toHaveLength(0);expect(state.bindings).toHaveLength(1);
 });
 test('many and empty forms keep reachable controls with enlarged text and dark theme',async({page,request},info)=>{
  await request.get(`${fixture}/reset?mode=many`);await page.setViewportSize({width:1280,height:720});await page.evaluate(()=>localStorage.setItem('helpdesk.theme.preference','dark'));await openEditor(page);
  await page.evaluate(()=>{document.documentElement.style.fontSize='20px';});await page.locator('[data-task-id]').last().click();await expect(page.getByRole('textbox',{name:/^Task Name\*?$/})).toHaveValue(/Synthetic long workflow task 30/);
  await expect(page.getByRole('button',{name:'Save',exact:true})).toBeInViewport();await expect(page.getByRole('button',{name:'Cancel',exact:true})).toBeInViewport();
  await page.screenshot({path:info.outputPath('after-many-dark-enlarged-1280x720.png')});await page.getByRole('button',{name:'Cancel',exact:true}).click();
  await request.get(`${fixture}/reset?mode=empty`);await page.evaluate(()=>{document.documentElement.style.fontSize='';});await openEditor(page,false);await expect(page.getByText('No workflow tasks yet. Add a task to start.')).toBeVisible();
  await page.getByRole('button',{name:'Add task',exact:true}).click();await expect(page.getByRole('textbox',{name:/^Task Name\*?$/})).toHaveValue('New Task');await page.getByRole('button',{name:'Duplicate task',exact:true}).click();await expect(page.getByRole('textbox',{name:/^Task Name\*?$/})).toHaveValue('New Task (Copy)');await page.getByRole('button',{name:'Delete task',exact:true}).click();await expect(page.getByRole('textbox',{name:/^Task Name\*?$/})).toHaveValue('New Task');
 });
}
if(!baseline){
 test('dragging the navigator while editing keeps selection, unsaved values and dependency identity',async({page,request})=>{
  await openEditor(page);const second='bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';const first='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
  await page.locator(`[data-task-id='${second}']`).click();
  await expect(page.locator(`[data-task-id='${second}']`)).toHaveAttribute('aria-pressed','true');
  await expect(page.getByRole('textbox',{name:/^Task Name\*?$/})).toHaveValue('Review result');
  await page.getByRole('textbox',{name:/^Task Name\*?$/}).fill('Drag edited review');await page.getByRole('textbox',{name:/^Task Name\*?$/}).blur();
  await expect(page.locator(`[data-task-id='${second}']`)).toContainText('Drag edited review');
  // Use real mouse input: MudDropZone inserts a drop preview over the target
  // during native dragging, so a locator action would wait for that preview.
  const source=await page.locator(`[data-task-id='${second}']`).boundingBox();
  const target=await page.locator(`[data-task-id='${first}']`).boundingBox();
  expect(source).not.toBeNull();expect(target).not.toBeNull();
  await page.mouse.move(source!.x+16,source!.y+20);await page.mouse.down();
  await page.mouse.move(target!.x+20,target!.y+5,{steps:12});
  await page.mouse.move(target!.x+22,target!.y+6);await page.mouse.up();
  await expect(page.locator('[data-task-id]').first()).toHaveAttribute('data-task-id',second);
  await expect(page.getByRole('textbox',{name:/^Task Name\*?$/})).toHaveValue('Drag edited review');await expect(page.locator(`[data-task-id='${second}']`)).toHaveAttribute('aria-pressed','true');
  await page.getByRole('button',{name:'Save',exact:true}).click();await expect(page.locator('.request-form-builder-dialog')).toBeHidden();
  const schema=JSON.parse((await(await request.get(`${fixture}/state`)).json()).form.jsonSchema);expect(schema.tasks[0]).toMatchObject({id:second,name:'Drag edited review',dependsOn:[first]});
 });
}

if(!baseline){
 test('validation focuses approval, dependency and numeric actions on previously hidden tasks',async({page,request})=>{
  for(const mode of ['invalid-approval','cycle','negative-sla']){
   await request.get(`${fixture}/reset?mode=${mode}`);await openEditor(page);await page.getByRole('button',{name:'Save',exact:true}).click();
   if(mode==='invalid-approval'){await expect(page.getByRole('textbox',{name:/^Task Name\*?$/})).toHaveValue('Approve completion');await expect(page.getByRole('button',{name:'Browse',exact:true})).toBeFocused();}
   else if(mode==='cycle'){await expect(page.getByRole('combobox',{name:'Depends On',exact:true})).toBeFocused();await expect(page.locator('.request-form-task-editor-content [role=alert]')).toContainText('Circular dependency');}
   else {await expect(page.getByRole('textbox',{name:/^Task Name\*?$/})).toHaveValue('Review result');await expect(page.getByLabel('Task SLA (minutes)',{exact:true})).toBeFocused();}
   expect((await(await request.get(`${fixture}/state`)).json()).saves).toHaveLength(0);await page.getByRole('button',{name:'Cancel',exact:true}).click();
  }
 });
}

if(!baseline){
 test('modern HA and five-node jobs retain exact saved connection and canonical IDs after save and reopen',async({page,request})=>{
  for(const [name,id,summary] of [['Synthetic HA job','9007199254740993','HA group: Synthetic HA pool (one eligible worker)'],['Synthetic five-node job','9007199254740995','5 selected nodes']]){
   await request.get(`${fixture}/reset?mode=modern`);await openEditor(page);
   await page.getByRole('button',{name:'Browse jobs',exact:true}).click();
   const browser=page.getByRole('dialog').last();await expect(browser).toContainText('Browse NetRatel jobs');
   await browser.getByText(name,{exact:true}).click();await browser.getByRole('button',{name:'Select job',exact:true}).click();
   await expect(page.locator('.request-form-step-automation-panel')).toContainText(summary);
   await page.getByRole('button',{name:'Save',exact:true}).click();await expect(page.locator('.request-form-builder-dialog')).toBeHidden();
   const state=await(await request.get(`${fixture}/state`)).json();expect(state.bindings[0]).toMatchObject({systemConnectionId:'11111111-1111-1111-1111-111111111111',orchestrationRequestDefinitionId:id,orchestrationJobDefinitionId:id});
   await openEditor(page);await expect(page.getByRole('textbox',{name:'NetRatel job',exact:true})).toHaveValue(new RegExp(name));await expect(page.locator('.request-form-step-automation-panel')).toContainText(summary);
   await page.getByRole('button',{name:'Cancel',exact:true}).click();await expect(page.locator('.request-form-builder-dialog')).toBeHidden();
  }
 });
 test('empty catalogue stays browsable and real-target creation retains its draft on a structured failure',async({page,request})=>{
  await request.get(`${fixture}/reset?mode=modern-empty`);await openEditor(page);
  await page.getByRole('button',{name:'Browse jobs',exact:true}).click();const browser=page.getByRole('dialog').last();await expect(browser).toContainText('No NetRatel jobs match');await browser.getByRole('button',{name:'Refresh jobs',exact:true}).click();await expect(browser).toContainText('No NetRatel jobs match');await browser.getByRole('button',{name:'Cancel',exact:true}).click();
  await page.getByRole('button',{name:'Create job',exact:true}).click();const create=page.getByRole('dialog').last();await expect(create).toContainText('Create NetRatel job');
  await create.getByRole('textbox',{name:/^Job name/}).fill('Synthetic authored job');await create.getByRole('textbox',{name:/^Command\*?$/}).fill('printf synthetic');
  await create.getByRole('combobox',{name:'Nodes',exact:true}).click();await page.getByText('Synthetic worker 1',{exact:true}).click();await create.getByRole('combobox',{name:'Nodes',exact:true}).click();
  await request.get(`${fixture}/fault?mode=job-create`);await create.getByRole('button',{name:'Create job',exact:true}).click();await expect(create).toContainText('target_unavailable');await expect(create).toContainText('fixture-create-reference');
  await expect(create.getByRole('textbox',{name:/^Job name/})).toHaveValue('Synthetic authored job');await expect(create.getByRole('textbox',{name:/^Command\*?$/})).toHaveValue('printf synthetic');
  await create.getByRole('button',{name:'Create job',exact:true}).click();await expect(page.getByRole('textbox',{name:'NetRatel job',exact:true})).toHaveValue(/Synthetic authored job/);
  await page.getByRole('button',{name:'Save',exact:true}).click();await expect(page.locator('.request-form-builder-dialog')).toBeHidden();
  const state=await(await request.get(`${fixture}/state`)).json();expect(state.creations).toHaveLength(2);expect(state.creations[1]).toMatchObject({organizationId:'org-1',connectionId:'11111111-1111-1111-1111-111111111111',payload:{clientIdentity:'',runner:'bash',command:'printf synthetic',tenantId:17,target:{source:2,agentIds:['eeeeeeee-eeee-eeee-eeee-000000000001']}}});expect(state.bindings[0].orchestrationRequestDefinitionId).toBe('9007199254740997');
 });
}
