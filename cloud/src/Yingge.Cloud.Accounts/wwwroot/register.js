const form = document.querySelector('#register-form');
const status = document.querySelector('#status');
const submit = document.querySelector('#submit');
form.addEventListener('submit', async event => {
  event.preventDefault();
  status.classList.remove('error');
  status.textContent = '';
  const inviteCode = document.querySelector('#invite').value.trim();
  const username = document.querySelector('#username').value.trim();
  const password = document.querySelector('#password').value;
  if (!inviteCode || !username || !password) {
    status.textContent = '请填写所有项目。'; status.classList.add('error'); return;
  }
  submit.disabled = true;
  try {
    const response = await fetch('/api/v1/auth/register', {
      method: 'POST', headers: {'Content-Type': 'application/json'},
      body: JSON.stringify({inviteCode, username, password}), cache: 'no-store'
    });
    if (!response.ok) {
      const problem = await response.json().catch(() => ({}));
      throw new Error(problem.detail || problem.title || '注册失败，请检查邀请码或稍后再试。');
    }
    form.reset();
    status.textContent = '账号已创建。现在可以在映格桌面软件的“设置 → 云端账号”登录。';
  } catch (error) {
    status.classList.add('error');
    status.textContent = error.message;
  } finally { submit.disabled = false; }
});
