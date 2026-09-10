import {$,api,refreshAccount,toast} from './app.js';

export function initAccount(account){
 $('account-username').value=account.username;
 $('rename-user-form').onsubmit=async event=>{
  event.preventDefault();$('rename-user').disabled=true;$('account-error').textContent='';
  try{const result=await api('/api/auth/username','PUT',{username:$('account-username').value});const updated=await refreshAccount();$('account-username').value=updated.username;toast(result.message);}
  catch(error){$('account-error').textContent=error.message;}
  finally{$('rename-user').disabled=false;}
 };
 $('reset-password').onclick=()=>{$('password-form').reset();$('password-error').textContent='';$('password-dialog').showModal();$('current-password').focus();};
 $('cancel-password').onclick=()=>$('password-dialog').close();
 $('password-dialog').addEventListener('close',()=>$('password-form').reset());
 $('password-form').onsubmit=async event=>{
  event.preventDefault();$('password-error').textContent='';
  if($('new-password').value!==$('confirm-password').value){$('password-error').textContent='两次输入的新密码不一致';return;}
  $('save-password').disabled=true;
  try{await api('/api/auth/password','PUT',{currentPassword:$('current-password').value,newPassword:$('new-password').value});$('password-form').reset();location.replace('/login?passwordChanged=1');}
  catch(error){$('password-error').textContent=error.message;}
  finally{$('save-password').disabled=false;}
 };
 $('upload-avatar').onclick=()=>$('avatar-file').click();
 $('avatar-file').onchange=async()=>{
  const file=$('avatar-file').files[0];if(!file)return;
  $('upload-avatar').disabled=true;$('account-error').textContent='';
  let bitmap;
  try{
   if(!['image/png','image/jpeg','image/webp'].includes(file.type))throw new Error('请选择 PNG、JPG 或 WebP 图片');
   if(file.size>5*1024*1024)throw new Error('图片不能超过 5 MB');
   try{bitmap=await createImageBitmap(file);}catch{throw new Error('无法读取这张图片，请选择其他图片');}
   const canvas=document.createElement('canvas');canvas.width=canvas.height=96;
   const size=Math.min(bitmap.width,bitmap.height);
   canvas.getContext('2d').drawImage(bitmap,(bitmap.width-size)/2,(bitmap.height-size)/2,size,size,0,0,96,96);
   const result=await api('/api/auth/avatar','PUT',{image:canvas.toDataURL('image/png')});
   await refreshAccount();toast(result.message);
  }catch(error){$('account-error').textContent=error.message;}
  finally{bitmap?.close();$('avatar-file').value='';$('upload-avatar').disabled=false;}
 };
}
