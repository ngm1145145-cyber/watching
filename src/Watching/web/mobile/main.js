/* Watching 手机客户端界面 */
(function () {
  'use strict';

  var $ = function (id) { return document.getElementById(id); };

  var login = $('login'), viewerEl = $('viewer'), hud = $('hud');
  var canvas = $('screen');
  var statPill = $('statPill'), detailPill = $('detailPill');
  var reconnectBox = $('reconnect');
  var loginErr = $('loginErr');

  var viewer = null;
  var qualityLevel = 1; // 0 流畅 / 1 中 / 2 高清
  var qualities = [
    { q: 45, w: 1080, label: '流畅' },
    { q: 65, w: 1600, label: '中' },
    { q: 85, w: 0, label: '高清' }
  ];
  var hudTimer = null;
  var pseudoFull = false;

  // ---------------- 登录 ----------------

  var saved = null;
  try { saved = JSON.parse(localStorage.getItem('watching.last') || 'null'); } catch (e) { }

  var params = new URLSearchParams(location.search);
  var urlHost = params.get('ip') || params.get('host');
  var urlPort = params.get('port');
  var urlPwd = params.get('pwd');

  if (saved && saved.remember) {
    $('host').value = saved.host || '';
    $('port').value = saved.port || 8899;
    $('pwd').value = saved.pwd || '';
    $('remember').checked = true;
  } else {
    $('host').value = location.hostname || '';
  }
  if (urlHost) $('host').value = urlHost;
  if (urlPort) $('port').value = urlPort;
  if (urlPwd) $('pwd').value = urlPwd;

  // 密码输入框
  var pwdRowShown = false;
  function showPwdRow(show) {
    pwdRowShown = show;
    $('pwd').classList.toggle('hidden', !show);
    $('pwdLabel').classList.toggle('hidden', !show);
  }

  $('go').addEventListener('click', startConnect);
  ['host', 'port', 'pwd'].forEach(function (id) {
    $(id).addEventListener('keydown', function (e) { if (e.key === 'Enter') startConnect(); });
  });

  function buildUrl(host, port, pwd) {
    var h = (host || '').trim();
    if (!h) return null;
    h = h.replace(/^https?:\/\//i, '').replace(/\/.*$/, '');
    var p = parseInt(port, 10) || 8899;
    var proto = location.protocol === 'https:' ? 'wss://' : 'ws://';
    var url = proto + h + ':' + p + '/ws';
    if (pwd) url += '?pwd=' + encodeURIComponent(pwd);
    return url;
  }

  function startConnect() {
    var host = $('host').value.trim();
    var port = $('port').value.trim() || '8899';
    var pwd = $('pwd').value;
    var url = buildUrl(host, port, pwd);
    loginErr.textContent = '';

    if (!url) { loginErr.textContent = '请输入服务端 IP 地址'; return; }

    if ($('remember').checked) {
      try {
        localStorage.setItem('watching.last', JSON.stringify({ host: host, port: port, pwd: pwd, remember: true }));
      } catch (e) { }
    } else {
      try { localStorage.removeItem('watching.last'); } catch (e) { }
    }

    showViewer();
    viewer.connect(url);
    sendQuality();
  }

  function showViewer() {
    login.classList.add('hidden');
    viewerEl.classList.remove('hidden');
    setTimeout(function () { viewer.resize(); }, 30);
    showHud(true);
  }

  function backToLogin() {
    viewer.close();
    viewerEl.classList.add('hidden');
    login.classList.remove('hidden');
  }

  // ---------------- Viewer ----------------

  viewer = new Watching.Viewer(canvas, {
    kind: 'mobile',
    name: (navigator.userAgent.match(/(iPhone|iPad|Android|Mobile)/) || ['手机'])[0],
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
        detailPill.textContent = '服务端 ' + (msg.name || '') + ' · ' + (msg.sw || '?') + '×' + (msg.sh || '?') +
          (msg.remote ? ' · 可远程控制' : '');
        if (msg.fps) statPill.textContent = '已连接';
      } else if (msg.t === 'error') {
        loginErr.textContent = msg.msg || '';
        if (msg.msg && msg.msg.indexOf('密码') >= 0) {
          backToLogin();
          showPwdRow(true);
          loginErr.textContent = msg.msg;
        }
      } else if (msg.t === 'settings') {
        detailPill.textContent = (detailPill.textContent || '') + '';
      }
    },
    onStats: function (s) {
      var size = viewer.frameSize();
      var text = s.fps.toFixed(0) + ' fps · ' + s.kbps.toFixed(0) + ' KB/s';
      if (size) text += ' · ' + size.w + '×' + size.h;
      statPill.textContent = text;
      statPill.className = 'pill ok';
    },
    onTap: function () { toggleHud(); },
    onDoubleTap: function () { toggleFullscreen(); }
  });

  viewer.attachGestures(canvas);

  function sendQuality() {
    var q = qualities[qualityLevel];
    viewer.send({ t: 'quality', quality: q.q, maxWidth: q.w, fps: qualityLevel === 0 ? 12 : (qualityLevel === 1 ? 20 : 30) });
  }

  // ---------------- HUD ----------------

  function showHud(autoHide) {
    hud.classList.add('show');
    if (hudTimer) clearTimeout(hudTimer);
    if (autoHide !== false) {
      hudTimer = setTimeout(function () { hud.classList.remove('show'); }, 4000);
    }
  }

  function toggleHud() {
    if (hud.classList.contains('show')) {
      hud.classList.remove('show');
      if (hudTimer) clearTimeout(hudTimer);
    } else {
      showHud(true);
    }
  }

  hud.addEventListener('click', function () { showHud(true); });

  // ---------------- 按钮 ----------------

  $('btnFull').addEventListener('click', function (e) { e.stopPropagation(); toggleFullscreen(); });
  $('btnFit').addEventListener('click', function (e) {
    e.stopPropagation();
    viewer.setFit(true);
    showHud(true);
  });
  $('btnSharp').addEventListener('click', function (e) {
    e.stopPropagation();
    qualityLevel = (qualityLevel + 1) % qualities.length;
    $('btnSharp').textContent = '画质:' + qualities[qualityLevel].label;
    sendQuality();
    showHud(true);
  });
  $('btnSnap').addEventListener('click', function (e) {
    e.stopPropagation();
    var data = viewer.snapshot();
    if (!data) return;
    var a = document.createElement('a');
    a.href = data;
    a.download = 'watching-' + new Date().toISOString().replace(/[:.]/g, '-') + '.jpg';
    a.click();
    showHud(true);
  });
  $('btnExit').addEventListener('click', function (e) {
    e.stopPropagation();
    backToLogin();
  });

  // ---------------- 全屏 ----------------

  function isFullscreen() {
    return document.fullscreenElement || document.webkitFullscreenElement || pseudoFull;
  }

  function toggleFullscreen() {
    if (isFullscreen()) {
      exitFullscreen();
    } else {
      enterFullscreen();
    }
  }

  function enterFullscreen() {
    var el = document.documentElement;
    var fn = el.requestFullscreen || el.webkitRequestFullscreen || el.webkitEnterFullscreen;
    if (fn) {
      try {
        var p = fn.call(el);
        if (p && p.catch) p.catch(function () { pseudoFullscreen(true); });
        if (p === undefined && !el.requestFullscreen) pseudoFullscreen(true);
      } catch (e) {
        pseudoFullscreen(true);
      }
    } else {
      pseudoFullscreen(true);
    }
    showHud(true);
    setTimeout(function () { viewer.resize(); }, 400);
  }

  function exitFullscreen() {
    if (document.fullscreenElement || document.webkitFullscreenElement) {
      var fn = document.exitFullscreen || document.webkitExitFullscreen;
      try { if (fn) fn.call(document); } catch (e) { }
    }
    pseudoFullscreen(false);
    setTimeout(function () { viewer.resize(); }, 300);
  }

  function pseudoFullscreen(on) {
    pseudoFull = on;
    document.body.classList.toggle('pseudo-full', on);
    document.body.style.overflow = on ? 'hidden' : '';
    window.scrollTo(0, 0);
  }

  ['fullscreenchange', 'webkitfullscreenchange'].forEach(function (ev) {
    document.addEventListener(ev, function () {
      pseudoFullscreen(!!(document.fullscreenElement || document.webkitFullscreenElement));
      setTimeout(function () { viewer.resize(); }, 200);
    });
  });

  document.addEventListener('keydown', function (e) {
    if (e.key === 'f' || e.key === 'F') toggleFullscreen();
    if (e.key === 'Escape' && pseudoFull) pseudoFullscreen(false);
  });

  // 有密码参数时直接连
  if (urlHost && urlPwd) {
    setTimeout(startConnect, 50);
  } else if (urlHost) {
    setTimeout(startConnect, 50);
  }
})();
