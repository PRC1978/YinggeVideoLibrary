const form=document.querySelector('#login-form'),status=document.querySelector('#status'),button=form.querySelector('button');
let token='';
const card=document.querySelector('.login-card'),dashboard=document.createElement('section');
dashboard.hidden=true;dashboard.setAttribute('aria-label','我的账号');card.append(dashboard);
const node=(tag,text)=>{const n=document.createElement(tag);n.textContent=text;return n;};
const notice=node('p','');notice.className='status';notice.setAttribute('role','status');
function action(text,run){const b=node('button',text);b.type='button';b.className='secondary';b.addEventListener('click',async()=>{b.disabled=true;try{await run();}catch(e){notice.textContent=e.message;}finally{b.disabled=false;}});return b;}
function showLogin(){dashboard.hidden=true;for(const c of card.children)if(c!==dashboard)c.hidden=false;}
async function api(path,method='GET',data){
 const headers={};if(token)headers.Authorization=`Bearer ${token}`;if(data!==undefined)headers['Content-Type']='application/json';
 const r=await fetch(`/api/v1/${path}`,{method,headers,body:data===undefined?undefined:JSON.stringify(data),cache:'no-store',credentials:'omit'});
 const body=r.status===204?null:await r.json().catch(()=>({}));
 if(!r.ok){if(r.status===401&&token){token='';showLogin();}throw new Error(body?.detail||body?.title||`请求失败：${r.status}`);}return body;
}
async function refresh(){
 const s=await api('auth/session'),reports=await api('reports');
 dashboard.replaceChildren(node('h1','我的账号'),node('p',`账号：${s.username}`),node('p',`会话有效至：${new Date(s.expiresAt).toLocaleString()}`),node('p',`近24小时上传：${s.uploadsLast24Hours} / ${s.dailyUploadLimit}`));
 dashboard.append(action('刷新',refresh),action('退出登录',async()=>{await api('auth/logout','POST');token='';showLogin();status.textContent='已退出登录。';}),node('h2','我的错误报告'),node('p','上传请使用桌面软件。刷新或关闭本页后需要重新登录。'));
 if(!reports.length)dashboard.append(node('p','尚无报告。'));
 for(const r of reports){const row=document.createElement('div');row.append(node('p',`${new Date(r.receivedAt).toLocaleString()} · ${r.messageCount}条 · ${r.byteSize}字节 · 到期${new Date(r.expiresAt).toLocaleString()}`));row.append(action('删除这份报告',async()=>{if(!window.confirm('删除这份错误报告？此操作无法撤销。'))return;await api(`reports/${encodeURIComponent(r.id)}`,'DELETE');await refresh();}));dashboard.append(row);}
 dashboard.append(notice);
}
form.addEventListener('submit',async e=>{
 e.preventDefault();status.className='status';status.textContent='正在登录…';button.disabled=true;
 try{const auth=await api('auth/login','POST',{username:document.querySelector('#username').value.trim(),password:document.querySelector('#password').value});document.querySelector('#password').value='';token=auth.token;
 if(auth.mustChangePassword){token='';status.textContent='请从管理入口修改初始密码后重新登录。';return;}
 for(const c of card.children)if(c!==dashboard)c.hidden=true;dashboard.hidden=false;dashboard.replaceChildren(node('p','正在加载账号…'));await refresh();
 }catch(err){status.className='status error';status.textContent=err.message;if(token){dashboard.replaceChildren(node('h1','账号暂不可用'),notice,action('重试',refresh),action('返回登录',()=>{token='';showLogin();}));notice.textContent=err.message;}}
 finally{button.disabled=false;}
});
