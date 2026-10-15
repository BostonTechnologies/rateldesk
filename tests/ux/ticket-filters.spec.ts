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
