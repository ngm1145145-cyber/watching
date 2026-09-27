/* Watching 电脑网页客户端界面 */
(function () {
  'use strict';

  var $ = function (id) { return document.getElementById(id); };
  var login = $('login'), viewerEl = $('viewer'), hud = $('hud'), canvas = $('screen');
  var statPill = $('statPill'), detailPill = $('detailPill'), reconnectBox = $('reconnect'), loginErr = $('loginErr');

  var qualities = [
    { q: 45, w: 1080, fps: 12, label: '流畅' },
    { q: 65, w: 1600, fps: 20, label: '中' },
    { q: 85, w: 0, fps: 30, label: '高清' }
  ];
  var qualityLevel = 1;
  var hudTimer = null;

  var saved = null;
  try { saved = JSON.parse(localStorage.getItem('watching.pc') || 'null'); } catch (e) { }
  var params = new URLSearchParams(location.search);

  if (saved) {
    $('host').value = saved.host || '';
    $('port').value = saved.port || 8899;
    $('pwd').value = saved.pwd || '';
  } else {
    $('host').value = params.get('ip') || location.hostname || '';
    $('port').value = params.get('port') || '8899';
  }
  if (params.get('pwd')) $('pwd').value = params.get('pwd');

  var viewer = new Watching.Viewer(canvas, {
    kind: 'web',
    name: 'PC浏览器',
    wheelZoom: true,
    onOpen: function () {
      reconnectBox.classList.add('hidden');
      statPill.textContent = '已连接';
      statPill.className = 'pill ok';
      sendQuality();
    },
    onClose: function () {
      statPill.textContent = '已断开';
      statPill.className = 'pill bad';
      reconnectBox.classList.remove('hidden');
    },
    onError: function () {
      statPill.textContent = '连接失败';
      statPill.className = 'pill bad';
    },
    onMessage: function (msg) {
      if (msg.t === 'welcome') {
        detailPill.textContent = '服务端 ' + (msg.name || '') + ' · 屏幕 ' + (msg.sw || '?') + '×' + (msg.sh || '?') +
          (msg.remote ? ' · 允许远程控制' : '');
      } else if (msg.t === 'error') {
        loginErr.textContent = msg.msg || '';
        if ((msg.msg || '').indexOf('密码') >= 0) {
          backToLogin();
          $('pwd').classList.remove('hidden');
          $('pwdLabel').classList.remove('hidden');
          loginErr.textContent = '服务端要求访问密码，请填写后重试。';
        }
      }
    },
    onStats: function (s) {
      var size = viewer.frameSize();
      var text = s.fps.toFixed(0) + ' fps · ' + s.kbps.toFixed(0) + ' KB/s';
      if (size) text += ' · ' + size.w + '×' + size.h;
      statPill.textContent = text;
      statPill.className = 'pill ok';
    },
    onTap: function () { showHud(true); }
  });

  viewer.attachGestures(canvas);

  function sendQuality() {
    var q = qualities[qualityLevel];
    viewer.send({ t: 'quality', quality: q.q, maxWidth: q.w, fps: q.fps });
  }

  function showHud(autoHide) {
    hud.classList.add('show');
    if (hudTimer) clearTimeout(hudTimer);
    if (autoHide !== false) hudTimer = setTimeout(function () { hud.classList.remove('show'); }, 3500);
  }

  function backToLogin() {
    viewer.close();
    viewerEl.classList.add('hidden');
    login.classList.remove('hidden');
    if (document.fullscreenElement) document.exitFullscreen();
  }

  function startConnect() {
    var host = $('host').value.trim().replace(/^https?:\/\//i, '').replace(/\/.*$/, '');
    var port = parseInt($('port').value, 10) || 8899;
    var pwd = $('pwd').value;
    loginErr.textContent = '';
    if (!host) { loginErr.textContent = '请输入服务端 IP 地址'; return; }

    try { localStorage.setItem('watching.pc', JSON.stringify({ host: host, port: port, pwd: pwd })); } catch (e) { }

    var url = (location.protocol === 'https:' ? 'wss://' : 'ws://') + host + ':' + port + '/ws';
    if (pwd) url += '?pwd=' + encodeURIComponent(pwd);

    login.classList.add('hidden');
    viewerEl.classList.remove('hidden');
    setTimeout(function () { viewer.resize(); }, 30);
    viewer.connect(url);
    showHud(true);
  }

  $('go').addEventListener('click', startConnect);
  ['host', 'port', 'pwd'].forEach(function (id) {
    $(id).addEventListener('keydown', function (e) { if (e.key === 'Enter') startConnect(); });
  });

  $('btnFit').addEventListener('click', function (e) { e.stopPropagation(); viewer.setFit(true); showHud(true); });
  $('btnSharp').addEventListener('click', function (e) {
    e.stopPropagation();
    qualityLevel = (qualityLevel + 1) % qualities.length;
    $('btnSharp').textContent = '画质:' + qualities[qualityLevel].label;
    sendQuality();
    showHud(true);
  });
  $('btnFull').addEventListener('click', function (e) {
    e.stopPropagation();
    if (document.fullscreenElement) document.exitFullscreen();
    else document.documentElement.requestFullscreen().catch(function () { });
    showHud(true);
  });
  $('btnExit').addEventListener('click', function (e) { e.stopPropagation(); backToLogin(); });

  document.addEventListener('keydown', function (e) {
    if (e.key === 'F11') {
      e.preventDefault();
      if (document.fullscreenElement) document.exitFullscreen();
      else document.documentElement.requestFullscreen().catch(function () { });
    } else if (e.key === 'f' || e.key === 'F') {
      viewer.setFit(true);
    }
  });

  document.addEventListener('fullscreenchange', function () {
    setTimeout(function () { viewer.resize(); }, 200);
  });

  if (params.get('ip')) setTimeout(startConnect, 50);
})();
