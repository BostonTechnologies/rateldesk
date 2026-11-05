import {test,expect} from '@playwright/test';
const fixture='http://127.0.0.1:18330/fixture';
test.beforeEach(async({page,request})=>{
 page.on('pageerror',error=>console.error('Browser error:',error.message));
 page.on('console',message=>{if(message.type()==='error')console.error('Browser console:',message.text());});
 await request.get(fixture+'/reset');await page.goto('/auth/development',{waitUntil:'domcontentloaded'});
});
for(const module of ['incidents','requests','changes']) {
 test(`${module} opening a ticket and browser Back restores the exact filtered page`,async({page,request})=>{
  const listUrl=`/${module}?customerOrgId=org-b&q=Synthetic&page=2&pageSize=25&sort=trackingId&descending=false`;
  await page.goto(listUrl);
  await expect(page.getByText('26-30 of 30')).toBeVisible();
  await expect(page.getByRole('textbox',{name:'Search tickets'})).toHaveValue('Synthetic');
  await expect(page.getByRole('button',{name:'Remove Customer: Beta customer',exact:true})).toBeVisible();
  const originalUrl=page.url();
  const originalRows=await page.locator('tbody tr').allTextContents();
  await page.locator('tbody input[type=checkbox]').first().check();
  await expect(page.getByText('1 selected',{exact:true})).toBeVisible();
  // The incident subject is an email-preview button; the tracking cell navigates the row.
  await page.getByRole('cell',{name:'SYN-051',exact:true}).click();
  await expect(page).toHaveURL(new RegExp(`/${module}/row-51\\?`));
  expect(new URL(page.url()).searchParams.get('returnUrl')).toBe(new URL(originalUrl).pathname+new URL(originalUrl).search);
  await expect(page.getByText('SYN-051',{exact:true})).toBeVisible();
  await expect(page.getByText('Synthetic ticket 51',{exact:true})).toBeVisible();
  await expect.poll(async()=>(await(await request.get(fixture+'/state')).json()).details).toContain(`/api/v1/${module}/row-51`);
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
  await page.goBack();
  await expect(page).toHaveURL(originalUrl);
  await expect(page.getByText('26-30 of 30')).toBeVisible();
  await expect(page.getByRole('textbox',{name:'Search tickets'})).toHaveValue('Synthetic');
  await expect(page.getByRole('button',{name:'Remove Customer: Beta customer',exact:true})).toBeVisible();
  expect(await page.locator('tbody tr').allTextContents()).toEqual(originalRows);
  await expect(page.getByText('1 selected',{exact:true})).toHaveCount(0);
  await expect(page.locator('tbody input[type=checkbox]:checked')).toHaveCount(0);
 });
 test(`${module} filters the full dataset, clears selection and restores URL with Back`,async({page,request})=>{
  await page.goto('/'+module);
  await expect(page.getByTestId('app-main-content')).toHaveAttribute('data-interactive','true');
  await expect(page.getByText('1-25 of 60')).toBeVisible();
  await expect(page.getByText(/AI (involved|origin) only/)).toHaveCount(0);
  await page.locator('tbody input[type=checkbox]').first().check();
  await expect(page.getByText('1 selected',{exact:true})).toBeVisible();
  await page.getByRole('button',{name:'Filters (0)'}).click();
  await page.getByLabel('Customer', {exact:true}).fill('Beta');
  await page.getByRole('option',{name:'Beta customer',exact:true}).click();
  await expect(page.getByText('1-25 of 30')).toBeVisible();
  await expect(page.getByText('1 selected',{exact:true})).toHaveCount(0);
  await expect(page).toHaveURL(/customerOrgId=org-b/);
  await page.getByRole('textbox',{name:'Search tickets'}).fill('59');
  await expect(page.getByText('1-1 of 1')).toBeVisible();
  await expect(page.getByText('Synthetic ticket 59',{exact:true})).toBeVisible();
  await page.goBack();
  await expect(page.getByText('1-25 of 30')).toBeVisible();
  await page.goForward();
  await expect(page.getByText('Synthetic ticket 59',{exact:true})).toBeVisible();
  await page.reload();
  await expect(page.getByText('Synthetic ticket 59',{exact:true})).toBeVisible();
  await page.getByRole('button',{name:'Remove Customer: Beta customer',exact:true}).focus();
  await page.keyboard.press('Enter');
  await expect(page).not.toHaveURL(/customerOrgId/);
  await page.getByRole('button',{name:'Clear all',exact:true}).click();
  await expect(page.getByText('1-25 of 60')).toBeVisible();
  const state=await(await request.get(fixture+'/state')).json();
  expect(state.queries.every((q:string)=>!q.includes('aiOnly')&&!q.includes('aiInvolved'))).toBeTruthy();
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
 });
}
test('Back during pending search preserves the restored page and URL',async({page})=>{
 await page.goto('/incidents?page=2');await expect(page.getByText('26-50 of 60')).toBeVisible();
 const search=page.getByRole('textbox',{name:'Search tickets'});
 await search.fill('59');await expect(page.getByText('Synthetic ticket 59',{exact:true})).toBeVisible();
 await search.fill('pending');await page.goBack();
 await expect(page.getByText('26-50 of 60')).toBeVisible();
 await page.waitForTimeout(500);
 await expect(page).toHaveURL(/page=2/);await expect(search).toHaveValue('');
 await expect(page.getByText('26-50 of 60')).toBeVisible();
});
test('cancelled search cannot replace newer data; error differs from no matches',async({page,request})=>{
 await page.goto('/incidents');
 await expect(page.getByText('1-25 of 60')).toBeVisible();
 const search=page.getByRole('textbox',{name:'Search tickets'});
 await search.fill('slow');
 await expect.poll(async()=>JSON.stringify(await(await request.get(fixture+'/state')).json())).toContain('q=slow');
 await search.fill('59');await expect(page.getByText('Synthetic ticket 59',{exact:true})).toBeVisible();
 await page.waitForTimeout(1300);await expect(page.getByText('Synthetic ticket 59',{exact:true})).toBeVisible();
 await request.get(fixture+'/fault?mode=error');await search.fill('58');
 await expect(page.getByText('Unable to load tickets. Check the filters and try again.')).toBeVisible();
 await page.getByRole('button',{name:'Retry',exact:true}).click();await expect(page.getByText('Synthetic ticket 58',{exact:true})).toBeVisible();
 await search.fill('missing');await expect(page.getByText('No tickets match these filters.')).toBeVisible();
});
for(const module of ['incidents','requests','changes']) for(const width of [320,768,1440,1920]) for(const theme of ['light','dark']) {
 test(`responsive ${module} filter panel ${width}px ${theme}`,async({page},info)=>{
  await page.addInitScript(value=>localStorage.setItem('helpdesk.theme.preference',value),theme);
  await page.setViewportSize({width,height:900});
  await page.goto('/'+module);await expect(page.getByText('1-25 of 60')).toBeVisible();
  await expect(page.locator('html')).toHaveAttribute('data-helpdesk-theme',theme);
  await page.getByRole('button',{name:'Filters (0)'}).click();
  await expect(page.getByLabel('From (inclusive)')).toBeVisible();
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth+1)).toBeTruthy();
  await page.locator('.ticket-filters').screenshot({path:info.outputPath(`filters-panel-${width}-${theme}.png`)});
  await page.screenshot({path:info.outputPath(`filters-${width}-${theme}.png`),fullPage:true});
 });
}

test('multi-dimension controls, time validation, sorting and page size use server query state',async({page,request})=>{
 await page.goto('/incidents');await expect(page.getByText('1-25 of 60')).toBeVisible();
 await page.getByRole('button',{name:'Filters (0)'}).click();
 await page.getByLabel('For',{exact:true}).fill('Contact');await page.getByRole('option',{name:'Contact (contact@example.test)',exact:true}).click();
 await page.getByLabel('Priority',{exact:true}).click();await page.getByRole('option',{name:'High',exact:true}).click();await page.keyboard.press('Escape');
 await expect(page.getByText('1-25 of 30')).toBeVisible();
 await page.getByLabel('State',{exact:true}).click();await page.getByRole('option',{name:'New',exact:true}).click();await page.keyboard.press('Escape');
 await page.getByLabel('Unassigned',{exact:true}).check();
 await page.getByLabel('Assignee',{exact:true}).fill('technician');await page.getByRole('option',{name:'Synthetic technician',exact:true}).click();
 // Wait for the URL-driven filter state to hydrate before editing its time draft.
 await expect(page).toHaveURL(url=>url.searchParams.get('assigneeId')==='staff');
 await expect(page.getByRole('button',{name:'Remove Assignee: Synthetic technician',exact:true})).toBeVisible();
 await page.getByLabel('From (inclusive)').fill('2026-10-16');await page.getByLabel('To (exclusive)').fill('2026-10-14');
 await page.getByRole('button',{name:'Apply time range'}).click();await expect(page.getByText('From must be earlier than To.')).toBeVisible();
 await page.getByLabel('From (inclusive)').fill('2026-10-15');await page.getByLabel('To (exclusive)').fill('2026-10-15');
 await page.getByLabel('Timezone',{exact:true}).fill('Africa/Johannesburg');await page.getByRole('button',{name:'Apply time range'}).click();
 await expect(page.getByText('1-25 of 30')).toBeVisible();
 await page.getByRole('combobox',{name:'Sort by',exact:true}).click();await page.getByRole('option',{name:'Tracking ID',exact:true}).click();
 await page.getByLabel('Descending',{exact:true}).uncheck();
 await expect(page.getByText('SYN-000',{exact:true})).toBeVisible();
 await page.locator('.mud-table-pagination-select').click();await page.getByRole('option',{name:'50',exact:true}).click();
 await expect(page.getByText('1-30 of 30')).toBeVisible();
 const queries=(await(await request.get(fixture+'/state')).json()).queries;const last=new URL(queries.at(-1),'http://fixture.test');
 expect(last.searchParams.get('pageSize')).toBe('50');expect(last.searchParams.get('page')).toBe('1');
 expect(last.searchParams.get('forId')).toBe('contact');expect(last.searchParams.get('assigneeId')).toBe('staff');
 expect(last.searchParams.get('stateValues')).toBe('New');expect(last.searchParams.get('priority')).toBe('High');
 expect(last.searchParams.get('from')).toContain('2026-10-14T22:00:00');expect(last.searchParams.get('to')).toContain('2026-10-15T22:00:00');
});

for (const module of ['incidents','requests','changes']) {
 if(module!=='changes')test(`${module} denied state write stays unavailable after readable refresh and filters`,async({page,request})=>{
  await page.goto('/'+module);await expect(page.getByText('1-25 of 60')).toBeVisible();
  const trigger=page.locator('tbody tr').first().locator('button.state-trigger');
  await trigger.click();await request.get(fixture+'/permissions?mode=reader');
  await page.getByRole('menuitem',{name:'In Progress',exact:true}).click();
  await expect(page.getByRole('alert').filter({hasText:'Reload this page to refresh access'})).toBeVisible();
  await expect(page.getByRole('menuitem',{name:'In Progress',exact:true})).toHaveCount(0);
  await page.getByRole('menuitem',{name:'Reload current state',exact:true}).click();
  await expect(trigger).toHaveAttribute('aria-expanded','false');await trigger.click();
  await expect(page.getByRole('alert').filter({hasText:'Reload this page to refresh access'})).toBeVisible();
  await expect(page.locator('.ticket-state-popover.mud-popover-open .state-popup-choice')).toHaveCount(0);
  await page.keyboard.press('Escape');await expect(trigger).toBeFocused();
  await page.getByRole('textbox',{name:'Search tickets'}).fill('Synthetic');
  await expect(page).toHaveURL(url=>url.searchParams.get('search')==='Synthetic');await expect(trigger).toHaveAttribute('aria-expanded','false');
  await trigger.click();await expect(page.getByRole('alert').filter({hasText:'Reload this page to refresh access'})).toBeVisible();
  await expect(page.locator('.ticket-state-popover.mud-popover-open .state-popup-choice')).toHaveCount(0);
  const state=await(await request.get(fixture+'/state')).json();expect(state.mutations).toHaveLength(1);expect(state.details).toHaveLength(1);
  expect(state.rows.find(item=>item.id==='row-59').state).toBe(0);
 });
 test(`${module} state destination is reachable and applied with only the keyboard`,async({page,request})=>{
  await page.goto('/'+module);await expect(page.getByText('1-25 of 60')).toBeVisible();
  const trigger=page.locator('tbody tr').first().locator('button.state-trigger');
  await expect(trigger).toBeEnabled();
  await trigger.focus();await page.keyboard.press('ArrowDown');
  const first=page.getByRole('menuitem',{name:module==='changes'?'Submit':'Waiting Reply',exact:true});
  await expect(first).toBeFocused();
  expect((await(await request.get(fixture+'/state')).json()).mutations).toHaveLength(0);
  await page.keyboard.press('Enter');
  await expect(trigger).toHaveText(module==='changes'?/Pending Approval/:/Waiting Reply/);
  expect((await(await request.get(fixture+'/state')).json()).mutations).toHaveLength(1);
 });
 test(`${module} state popup supports keyboard dismissal, one confirmed write and persisted readback`,async({page,request})=>{
  await page.goto('/'+module);
  await expect(page.getByText('1-25 of 60')).toBeVisible();
  const row=page.locator('tbody tr').filter({has:page.getByText('SYN-059',{exact:true})});
  const trigger=row.locator('button.state-trigger');
  const original=page.url();
  await expect(trigger).toBeEnabled();
  await page.evaluate(()=>{
   const events={keys:[] as {key:string;prevented:boolean}[],clicks:[] as number[]};
   (window as any).__stateActivationEvents=events;
   window.addEventListener('keydown',event=>{if((event.key==='Enter'||event.key===' ')&&(event.target as Element).closest('button.state-trigger')) events.keys.push({key:event.key,prevented:event.defaultPrevented});});
   window.addEventListener('click',event=>{if((event.target as Element).closest('button.state-trigger')) events.clicks.push(event.detail);});
  });
  // Assistive-technology clicks remain valid; keyboard opening must not also
  // dispatch the native button click and toggle the newly opened menu closed.
  await trigger.evaluate(element=>(element as HTMLButtonElement).click());
  await expect(page.getByText(module==='changes'?'Current: Draft':'Current: New',{exact:true})).toBeVisible();
  await page.keyboard.press('Escape');await expect(trigger).toBeFocused();
  await trigger.focus();await page.keyboard.press('Enter');
  await expect(page.getByText(module==='changes'?'Current: Draft':'Current: New',{exact:true})).toBeVisible();
  await expect(page.getByRole('menuitem',{name:module==='changes'?'Submit':'In Progress',exact:true})).toBeVisible();
  await expect(page.locator('.ticket-state-popover').getByRole('alert')).toHaveCount(0);
  expect((await(await request.get(fixture+'/state')).json()).mutations).toHaveLength(0);
  await page.keyboard.press('Escape');
  await expect(trigger).toHaveAttribute('aria-expanded','false');
  await expect(trigger).toBeFocused();
  expect(page.url()).toBe(original);
  await page.keyboard.press('Space');
  await expect(page.getByRole('menuitem',{name:module==='changes'?'Submit':'Waiting Reply',exact:true})).toBeFocused();
  expect(await page.evaluate(()=>(window as any).__stateActivationEvents)).toEqual({keys:[{key:'Enter',prevented:true},{key:' ',prevented:true}],clicks:[0]});
  await page.keyboard.press('Escape');
  await expect(trigger).toHaveAttribute('aria-expanded','false');
  await expect(trigger).toBeFocused();
  await trigger.click();
  const outside=await page.getByRole('heading',{name:module[0].toUpperCase()+module.slice(1),exact:true}).boundingBox();
  const outsidePoint={x:outside!.x+outside!.width/2,y:outside!.y+outside!.height/2};
  await expect.poll(()=>page.evaluate(point=>document.elementFromPoint(point.x,point.y)?.classList.contains('ticket-state-overlay'),outsidePoint)).toBe(true);
  await page.mouse.click(outsidePoint.x,outsidePoint.y);
  await expect(trigger).toHaveAttribute('aria-expanded','false');
  await expect(trigger).toBeFocused();
  expect((await(await request.get(fixture+'/state')).json()).mutations).toHaveLength(0);
  await trigger.click();
  await expect.poll(()=>page.evaluate(()=>document.elementFromPoint(1,1)?.classList.contains('ticket-state-overlay'))).toBe(true);
  const layers=await page.evaluate(()=>({
   popup:parseInt(getComputedStyle(document.querySelector('.ticket-state-popover.mud-popover-open')!).zIndex),
   overlay:parseInt(getComputedStyle(document.querySelector('.ticket-state-overlay')!).zIndex),
   appbar:parseInt(getComputedStyle(document.querySelector('.mud-appbar')!).zIndex),
   dialog:parseInt(getComputedStyle(document.documentElement).getPropertyValue('--mud-zindex-dialog'))
  }));
  expect(layers.overlay).toBeGreaterThan(layers.appbar);expect(layers.popup).toBeGreaterThan(layers.overlay);expect(layers.popup).toBeLessThan(layers.dialog);
  await page.mouse.click(1,1);
  await expect(trigger).toHaveAttribute('aria-expanded','false');await expect(trigger).toBeFocused();
  expect((await(await request.get(fixture+'/state')).json()).mutations).toHaveLength(0);
  await trigger.click();
  await request.get(fixture+'/fault?mode=state-slow');
  const action=page.getByRole('menuitem',{name:module==='changes'?'Submit':'In Progress',exact:true});
  await action.dblclick();
  await expect(trigger).toBeDisabled();
  await expect(trigger).toHaveText(module==='changes'?/Pending Approval/:/In Progress/);
  await expect(trigger).toBeEnabled();
  let state=await(await request.get(fixture+'/state')).json();
  expect(state.mutations).toHaveLength(1);
  expect(state.mutations[0].path).toBe(`/api/v1/${module}/row-59/${module==='changes'?'lifecycle':'state'}`);
  expect(state.details).toEqual([]);
  expect(page.url()).toBe(original);
  await page.reload();
  await expect(page.locator('tbody tr').filter({has:page.getByText('SYN-059',{exact:true})}).locator('button.state-trigger')).toHaveText(module==='changes'?/Pending Approval/:/In Progress/);
  state=await(await request.get(fixture+'/state')).json();expect(state.mutations).toHaveLength(1);
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
 });
 test(`${module} rejected state update retains state and requires reload before retry`,async({page,request})=>{
  await page.goto('/'+module);await expect(page.getByText('1-25 of 60')).toBeVisible();
  const trigger=page.locator('tbody tr').first().locator('button.state-trigger');
  await trigger.click();await request.get(fixture+'/fault?mode=state-conflict');
  await page.getByRole('menuitem',{name:module==='changes'?'Submit':'In Progress',exact:true}).click();
  await expect(page.getByRole('alert').filter({hasText:/changed while|updated during/})).toBeVisible();
  await expect(trigger).toHaveText(module==='changes'?/Draft/:/New/);
  await expect(page.getByRole('menuitem',{name:module==='changes'?'Submit':'In Progress',exact:true})).toHaveCount(0);
  expect((await(await request.get(fixture+'/state')).json()).mutations).toHaveLength(1);
  await page.getByRole('menuitem',{name:'Reload current state',exact:true}).click();
  // The authoritative read refreshes the list and replaces its row component.
  await expect(trigger).toHaveAttribute('aria-expanded','false');
  await trigger.click();
  await page.getByRole('menuitem',{name:module==='changes'?'Submit':'In Progress',exact:true}).click();
  await expect(trigger).toHaveText(module==='changes'?/Pending Approval/:/In Progress/);
  expect((await(await request.get(fixture+'/state')).json()).mutations).toHaveLength(2);
 });
 test(`${module} lost mutation response sends once and requires authoritative readback`,async({page,request})=>{
  await page.goto('/'+module);await expect(page.getByText('1-25 of 60')).toBeVisible();
  const trigger=page.locator('tbody tr').first().locator('button.state-trigger');
  await trigger.click();await request.get(fixture+'/fault?mode=state-lost');
  const destination=module==='changes'?'Submit':'In Progress';
  await page.getByRole('menuitem',{name:destination,exact:true}).click();
  await expect(page.locator('.ticket-state-popover').getByRole('alert')).toContainText(/could not be (updated|confirmed)/);
  await expect(trigger).toHaveText(module==='changes'?/Draft/:/New/);
  await expect(page.getByRole('menuitem',{name:destination,exact:true})).toHaveCount(0);
  let state=await(await request.get(fixture+'/state')).json();expect(state.mutations).toHaveLength(1);
  const saved=state.rows.find(item=>item.id==='row-59');expect(module==='changes'?saved.lifecycleState:saved.state).toBe(module==='changes'?2:3);
  await page.getByRole('menuitem',{name:'Reload current state',exact:true}).click();
  await expect(trigger).toHaveText(module==='changes'?/Pending Approval/:/In Progress/);
  state=await(await request.get(fixture+'/state')).json();expect(state.mutations).toHaveLength(1);expect(state.details).toHaveLength(1);
  await page.reload();await expect(trigger).toHaveText(module==='changes'?/Pending Approval/:/In Progress/);
  expect((await(await request.get(fixture+'/state')).json()).mutations).toHaveLength(1);
 });
 test(`${module} completion refreshes filtered counts without losing valid search sort or page`,async({page,request})=>{
  if(module==='changes') await request.get(fixture+'/row?id=row-51&lifecycle=3');
  const url=`/${module}?customerOrgId=org-b&q=Synthetic&page=2&pageSize=25&sort=trackingId&descending=false`;
  await page.goto(url);await expect(page.getByText('26-30 of 30')).toBeVisible();
  const original=page.url();
  const row=page.locator('tbody tr').filter({has:page.getByText('SYN-051',{exact:true})});
  await row.locator('input[type=checkbox]').check();
  await expect(page.getByText('1 selected',{exact:true})).toBeVisible();
  await row.locator('button.state-trigger').click();
  await page.getByRole('menuitem',{name:module==='changes'?'Implemented - Success':'Resolved',exact:true}).click();
  await expect(page.getByText('26-29 of 29')).toBeVisible();
  await expect(page.getByText('SYN-051',{exact:true})).toHaveCount(0);
  await expect(page.getByText('1 selected',{exact:true})).toHaveCount(0);
  expect(page.url()).toBe(original);
  await expect(page.getByRole('textbox',{name:'Search tickets'})).toHaveValue('Synthetic');
  await expect(page.getByRole('region',{name:'Ticket results'})).toBeFocused();
  await page.getByRole('button',{name:`Show historic ${module}`,exact:true}).click();
  await expect(page.getByText('SYN-051',{exact:true})).toBeVisible();
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
 });
}

for(const module of ['incidents','requests','changes']) for(const width of [320,1440]) for(const theme of ['light','dark']) {
 test(`state popup ${module} ${width}px ${theme}`,async({page},info)=>{
  await page.addInitScript(value=>localStorage.setItem('helpdesk.theme.preference',value),theme);
  await page.setViewportSize({width,height:900});await page.goto('/'+module);
  await expect(page.getByText('1-25 of 60')).toBeVisible();
  const trigger=page.locator('tbody tr').first().locator('button.state-trigger');
  await trigger.click();
   const popup=page.locator('.ticket-state-popover.mud-popover-open');
  await expect(popup).toBeVisible();
  await expect(page.getByText(module==='changes'?'Current: Draft':'Current: New',{exact:true})).toBeVisible();
  const bounds=await popup.boundingBox();expect(bounds).not.toBeNull();
  expect(bounds!.x).toBeGreaterThanOrEqual(0);expect(bounds!.x+bounds!.width).toBeLessThanOrEqual(width+1);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth+1)).toBeTruthy();
  await popup.screenshot({path:info.outputPath(`state-popup-${module}-${width}-${theme}.png`)});
  await page.keyboard.press('Escape');await expect(trigger).toBeFocused();
 });
}

test('change popup explains outstanding approvals and template prerequisites',async({page,request})=>{
 await request.get(fixture+'/row?id=row-59&lifecycle=2&pending=true');
 await page.goto('/changes');await expect(page.getByText('1-25 of 60')).toBeVisible();
 const trigger=page.locator('tbody tr').first().locator('button.state-trigger');
 await trigger.click();
 await expect(page.getByText('Outstanding approvals must be completed in the change details.')).toBeVisible();
 await expect(page.getByRole('menuitem',{name:/Approved For Implementation/})).toHaveAttribute('aria-disabled','true');
 await expect(page.getByRole('menuitem',{name:'Open details and approvals',exact:true})).toBeVisible();
 await page.keyboard.press('Escape');
 await request.get(fixture+'/row?id=row-59&lifecycle=4&pending=false&template=false');await page.reload();
 await trigger.click();
 await expect(page.getByText('Complete the change template in the details before implementing.')).toHaveCount(2);
 expect((await(await request.get(fixture+'/state')).json()).mutations).toHaveLength(0);
});

test('read-only actors have state indicators without an update affordance',async({page,context,request})=>{
 await request.get(fixture+'/permissions?mode=reader');await context.clearCookies();await page.goto('/auth/development');
 for(const module of ['incidents','requests','changes']) {
  await page.goto('/'+module);await expect(page.getByText('1-25 of 60')).toBeVisible();
  await expect(page.locator('button.state-trigger')).toHaveCount(0);
 }
 expect((await(await request.get(fixture+'/state')).json()).mutations).toHaveLength(0);
});
