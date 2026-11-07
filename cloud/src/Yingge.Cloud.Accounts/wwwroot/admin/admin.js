const $ = selector => document.querySelector(selector);
let token = '';
let adminKey = '';
let tab = 'accounts';
const titles = {accounts:'账号', invites:'邀请', reports:'报告', audit:'操作记录'};

function message(selector, value, error = false) {
  const node = $(selector);
  node.textContent = value;
  node.classList.toggle('error', error);
}
async function request(path, method = 'GET', data, withKey = true) {
  const headers = {'Authorization': `Bearer ${token}`};
  if (withKey) headers['X-Admin-Key'] = adminKey;
  if (data !== undefined) headers['Content-Type'] = 'application/json';
  const response = await fetch(`/api/v1/${path}`, {
    method, headers, body: data === undefined ? undefined : JSON.stringify(data), cache:'no-store'
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    throw new Error(problem.detail || problem.title || `请求失败：${response.status}`);
  }
  return response.status === 204 ? null : response.json();
}
function show(view) {
  for (const id of ['login-view','password-view','dashboard-view'])
    $(`#${id}`).hidden = id !== view;
}
function td(row, value) {
  const cell = document.createElement('td'); cell.textContent = value ?? '—'; row.append(cell); return cell;
}
function date(value) { return value ? new Date(value).toLocaleString('zh-CN') : '—'; }
function draw(headers, rows, render) {
  const head = $('#table-head'), body = $('#table-body');
  head.replaceChildren(); body.replaceChildren();
  const hr = document.createElement('tr');
  for (const name of headers) { const th = document.createElement('th'); th.textContent = name; hr.append(th); }
  head.append(hr);
  if (!rows.length) { const tr = document.createElement('tr'); const cell = td(tr, '暂无记录'); cell.colSpan = headers.length; body.append(tr); return; }
  for (const item of rows) { const tr = document.createElement('tr'); render(tr,item); body.append(tr); }
}
async function overview() {
  const data = await request('admin/overview');
  if (data.mustChangePassword) { show('password-view'); return false; }
  const metricData = [
    ['账号总数',data.accounts],['待使用邀请码',data.pendingInvites],
    ['有效会话',data.activeSessions],['有效报告',data.reportsLive]
  ];
  const parent = $('#metrics'); parent.replaceChildren();
  for (const [label,value] of metricData) {
    const item = document.createElement('div'); item.className = 'metric';
    const number = document.createElement('strong'); number.textContent = value;
    const caption = document.createElement('span'); caption.textContent = label;
    item.append(number,caption); parent.append(item);
  }
  show('dashboard-view'); return true;
}
async function loadTab() {
  $('#section-title').textContent = titles[tab];
  $('#new-invite').hidden = tab !== 'invites';
  $('#invite-created').hidden = true;
  message('#dashboard-status','');
  const rows = await request(`admin/${tab}`);
  if (tab === 'accounts') draw(['账号','状态','加入时间','操作'],rows,(tr,item)=>{
    td(tr,item.username); td(tr,item.status === 'active' ? '正常' : '已停用'); td(tr,date(item.createdAt));
    const cell = td(tr,'');
    if (item.isAdmin) { cell.textContent = '管理员（受保护）'; return; }
    const button = document.createElement('button');
    const disable = item.status === 'active';
    button.textContent = disable ? '停用' : '启用';
    button.addEventListener('click',async()=>{
      if (!confirm(`${disable ? '停用' : '启用'}账号 ${item.username}？停用会立即撤销其会话。`)) return;
      button.disabled = true;
      try { await request(`admin/accounts/${encodeURIComponent(item.username)}/status/${disable ? 'disabled' : 'active'}`,'POST'); await overview(); await loadTab(); }
      catch(error) {message('#dashboard-status',error.message,true);button.disabled=false;}
    }); cell.append(button);
  });
  if (tab === 'invites') draw(['备注','创建时间','有效至','状态','操作'],rows,(tr,item)=>{
    td(tr,item.note); td(tr,date(item.createdAt)); td(tr,date(item.expiresAt));
    const pending = !item.usedAt && !item.revokedAt && new Date(item.expiresAt)>new Date();
    td(tr,item.usedAt ? '已使用' : item.revokedAt ? '已撤销' : pending ? '待使用' : '已过期');
    const cell = td(tr,''); if (!pending) return;
    const button=document.createElement('button');button.textContent='撤销';button.addEventListener('click',async()=>{
      if(!confirm('撤销这枚尚未使用的邀请码？')) return;
      try{await request(`admin/invites/${item.id}/revoke`,'POST');await overview();await loadTab();}
      catch(error){message('#dashboard-status',error.message,true);}
    });cell.append(button);
  });
  if (tab === 'reports') draw(['账号','收到时间','版本','消息数','大小','状态'],rows,(tr,item)=>{
    td(tr,item.username);td(tr,date(item.receivedAt));td(tr,item.clientVersion);
    td(tr,item.messageCount);td(tr,`${item.byteSize} B`);td(tr,item.deletedAt?'已删除':'保留中');
  });
  if (tab === 'audit') draw(['时间','事件','账号 ID'],rows,(tr,item)=>{
    td(tr,date(item.at));td(tr,item.eventName);td(tr,item.accountId);
  });
}

$('#login-form').addEventListener('submit',async event=>{
  event.preventDefault();message('#login-status','');
  const button=event.currentTarget.querySelector('button');button.disabled=true;
  try{
    const username=$('#username').value.trim(),password=$('#password').value;
    adminKey='';
    const response=await fetch('/api/v1/auth/login',{method:'POST',headers:{'Content-Type':'application/json'},
      body:JSON.stringify({username,password}),cache:'no-store'});
    if(!response.ok)throw new Error('账号名或密码不正确，或账号暂时锁定。');
    token=(await response.json()).token;
    $('#password').value='';
    if(await overview())await loadTab();
  }catch(error){token='';adminKey='';message('#login-status',error.message,true);}
  finally{button.disabled=false;}
});
$('#password-form').addEventListener('submit',async event=>{
  event.preventDefault();
  try{
    await request('admin/change-password','POST',{
      currentPassword:$('#old-password').value,newPassword:$('#new-password').value
    });
    token='';adminKey='';event.currentTarget.reset();show('login-view');
    message('#login-status','密码已修改，请用新密码重新登录。');
  }catch(error){message('#password-status',error.message,true);}
});
$('#logout').addEventListener('click',()=>{token='';adminKey='';show('login-view');message('#login-status','已退出当前页面。');});
$('#refresh').addEventListener('click',async()=>{try{await overview();await loadTab();}catch(error){message('#dashboard-status',error.message,true);}});
document.querySelectorAll('[data-tab]').forEach(button=>button.addEventListener('click',async()=>{
  tab=button.dataset.tab;
  document.querySelectorAll('[data-tab]').forEach(node=>node.classList.toggle('active',node===button));
  try{await loadTab();}catch(error){message('#dashboard-status',error.message,true);}
}));
$('#new-invite').addEventListener('click',async()=>{
  const note=prompt('邀请码备注（可留空）','');if(note===null)return;
  try{
    const result=await request('admin/invites','POST',{note:note.trim(),hours:72});
    await overview();await loadTab();
    $('#invite-code').textContent=result.code;$('#invite-created').hidden=false;
  }catch(error){message('#dashboard-status',error.message,true);}
});
$('#copy-invite').addEventListener('click',async()=>{
  try{await navigator.clipboard.writeText($('#invite-code').textContent);message('#dashboard-status','邀请码已复制。');}
  catch{message('#dashboard-status','复制失败，请手动选取邀请码。',true);}
});
