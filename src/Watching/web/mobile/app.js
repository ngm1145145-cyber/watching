/* Watching 客户端核心逻辑（网页版：手机 + 电脑浏览器共用） */
(function () {
  'use strict';

  // ---------------- 帧协议 ----------------
  var HEADER = 4 + 8 + 256;
  var MAGIC = [0x57, 0x46, 0x30, 0x31]; // "WF01"
  var TILE_TABLE_HEADER = 8;   // "DT01" + 分块数
  var TILE_ENTRY = 8;          // x(2) + y(2) + 长度(4)

  // 解析一帧：整帧返回 {full: Blob}，增量返回 {tiles:[{x,y,blob}]}
  function parseFrame(buf) {
    if (!buf || buf.byteLength < HEADER) return null;
    var u8 = new Uint8Array(buf);
    if (u8[0] !== MAGIC[0] || u8[1] !== MAGIC[1] || u8[2] !== MAGIC[2] || u8[3] !== MAGIC[3]) return null;

    var dv = new DataView(buf);
    var seq = Number(dv.getBigInt64(4, true));

    var end = 12;
    while (end < 12 + 256 && u8[end] !== 0) end++;
    var meta = {};
    try { meta = JSON.parse(new TextDecoder('utf-8').decode(u8.subarray(12, end))); }
    catch (e) { return null; }

    var body = HEADER;

    if (meta.mode !== 'delta') {
      return {
        seq: seq, meta: meta, delta: false,
        blob: new Blob([buf.slice(body)], { type: 'image/jpeg' })
      };
    }

    // 增量帧：DT01 + 分块数 + 表项 + 各分块 JPEG
    if (buf.byteLength < body + TILE_TABLE_HEADER) return null;
    if (u8[body] !== 0x44 || u8[body + 1] !== 0x54 || u8[body + 2] !== 0x30 || u8[body + 3] !== 0x31) return null;

    var count = dv.getInt32(body + 4, true);
    if (count < 0 || count > 4096) return null;

    var p = body + TILE_TABLE_HEADER;
    var entries = [];
    for (var i = 0; i < count; i++) {
      var x = (u8[p] << 8) | u8[p + 1];
      var y = (u8[p + 2] << 8) | u8[p + 3];
      var len = dv.getUint32(p + 4, true);
      p += TILE_ENTRY;
      entries.push({ x: x, y: y, len: len });
    }

    var tiles = [];
    var dataStart = p;
    for (var j = 0; j < entries.length; j++) {
      var e = entries[j];
      if (dataStart + e.len > buf.byteLength) return null;
      tiles.push({
        x: e.x, y: e.y,
        blob: new Blob([buf.slice(dataStart, dataStart + e.len)], { type: 'image/jpeg' })
      });
      dataStart += e.len;
    }

    return { seq: seq, meta: meta, delta: true, tiles: tiles };
  }

  // ---------------- 观看端 ----------------
  function Viewer(canvas, opts) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d', { alpha: false });
    this.opts = opts || {};
    this.ws = null;
    this.url = '';
    this.image = null;
    this.pending = null;
    this.dirty = false;
    this.connected = false;
    this.manualClose = false;
    this.retry = 0;
    this.retryTimer = null;

    this.stats = { frames: 0, bytes: 0, lastTick: 0, fps: 0, kbps: 0, seq: -1, moved: [], lastMeta: null, total: 0 };

    this.fit = true;              // 适应窗口
    this.zoom = 1;                // 手动缩放
    this.offset = [0, 0];         // 手动平移
    this.showHud = true;
    this.hudTimer = null;
    this.lastTap = 0;
    this.touchState = null;

    var self = this;
    window.addEventListener('resize', function () { self.resize(); });
    document.addEventListener('visibilitychange', function () {
      if (!document.hidden) self.dirty = true;
    });
    if (typeof ResizeObserver === 'function') {
      new ResizeObserver(function () { self.resize(); }).observe(canvas);
    }
    requestAnimationFrame(function (t) { self.renderLoop(t); });
    this.resize();
  }

  Viewer.prototype.connect = function (url) {
    this.url = url;
    this.manualClose = false;
    this.open();
  };

  Viewer.prototype.open = function () {
    var self = this;
    try {
      var ws = new WebSocket(this.url);
      ws.binaryType = 'arraybuffer';
      this.ws = ws;

      ws.onopen = function () {
        self.connected = true;
        // 新一路连接：丢掉上一路的基准画面和序号，避免把新会话的分块贴到旧画面上
        self.base = null;
        self.baseSeq = -1;
        self.pending = null;
        self.retry = 0;
        self.dirty = true;
        self.send({
          t: 'hello', kind: self.opts.kind || 'web',
          name: self.opts.name || '', version: '1.0'
        });
        if (self.opts.onOpen) self.opts.onOpen();
      };

      ws.onmessage = function (ev) {
        if (typeof ev.data === 'string') { self.onText(ev.data); return; }
        var frame = parseFrame(ev.data);
        if (!frame) return;
        self.stats.frames++;
        self.stats.total++;
        self.stats.bytes += ev.data.byteLength;
        self.stats.seq = frame.seq;

        if (self.decoder) {
          // 预留：浏览器支持 ImageDecoder 时可以走更快的解码路径
          self.decodeBlob(frame);
        } else {
          self.decodeBlob(frame);
        }
      };

      ws.onclose = function () {
        self.connected = false;
        self.dirty = true;
        if (self.opts.onClose) self.opts.onClose();
        if (!self.manualClose) self.scheduleRetry();
      };

      ws.onerror = function () {
        if (self.opts.onError) self.opts.onError();
      };
    } catch (e) {
      this.scheduleRetry();
    }
  };

  Viewer.prototype.scheduleRetry = function () {
    var self = this;
    if (this.retryTimer) clearTimeout(this.retryTimer);
    this.retry = Math.min(this.retry + 1, 8);
    var delay = Math.min(1000 * this.retry, 6000);
    this.retryTimer = setTimeout(function () { if (!self.manualClose) self.open(); }, delay);
  };

  Viewer.prototype.close = function () {
    this.manualClose = true;
    if (this.retryTimer) clearTimeout(this.retryTimer);
    try { if (this.ws) this.ws.close(); } catch (e) { }
    this.ws = null;
    this.connected = false;
  };

  Viewer.prototype.send = function (obj) {
    try {
      if (this.ws && this.ws.readyState === 1) this.ws.send(JSON.stringify(obj));
    } catch (e) { }
  };

  Viewer.prototype.onText = function (text) {
    var msg;
    try { msg = JSON.parse(text); } catch (e) { return; }
    if (this.opts.onMessage) this.opts.onMessage(msg);
  };

  // ---- 解码 / 合成 ----
  //
  // 服务端会发两种帧：
  //   整帧（mode=full）  —— 直接作为新的基准画面
  //   增量帧（mode=delta）—— 只含变化的分块，贴到基准的对应位置
  // 所以这里维护一张离屏画布作为“当前完整画面”，屏幕上只做一次拷贝。

  Viewer.prototype.decodeBlob = function (frame) {
    var self = this;

    if (!frame.delta) {
      loadImage(frame.blob, function (img) {
        var off = document.createElement('canvas');
        off.width = img.naturalWidth;
        off.height = img.naturalHeight;
        off.getContext('2d').drawImage(img, 0, 0);

        self.base = off;
        self.baseSeq = frame.seq;
        self.pending = { canvas: off, meta: frame.meta };
        self.dirty = true;
        self.mark();
      });
      return;
    }

    // 增量帧：必须有基准，而且基准必须是同一块画面、同一尺寸、更新的序号。
    // 重连或改了画质/分辨率后如果基准还是上一路的画面，把分块贴上去会整片错位。
    if (!this.base) return;
    if (frame.seq <= this.baseSeq) return;
    if (frame.meta && (frame.meta.w !== this.base.width || frame.meta.h !== this.base.height)) return;

    var ctx = this.base.getContext('2d');
    var remaining = frame.tiles.length;
    var applied = false;

    if (remaining === 0) return;

    frame.tiles.forEach(function (t) {
      loadImage(t.blob, function (img) {
        try { ctx.drawImage(img, t.x, t.y); applied = true; } catch (e) { }
        if (--remaining === 0 && applied) {
          self.pending = { canvas: self.base, meta: frame.meta };
          self.dirty = true;
          self.mark();
        }
      }, function () {
        if (--remaining === 0 && applied) {
          self.pending = { canvas: self.base, meta: frame.meta };
          self.dirty = true;
          self.mark();
        }
      });
    });
  };

  function loadImage(blob, onload, onerror) {
    var url = URL.createObjectURL(blob);
    var img = new Image();
    img.onload = function () {
      try { URL.revokeObjectURL(url); } catch (e) { }
      onload(img);
    };
    img.onerror = function () {
      try { URL.revokeObjectURL(url); } catch (e) { }
      if (onerror) onerror();
    };
    img.src = url;
  }

  Viewer.prototype.mark = function () {
    this.stats.moved.push(Date.now());
  };

  // ---- 渲染 ----

  Viewer.prototype.resize = function () {
    var dpr = Math.min(window.devicePixelRatio || 1, 2);
    var w = this.canvas.clientWidth || window.innerWidth;
    var h = this.canvas.clientHeight || window.innerHeight;
    this.canvas.width = Math.max(1, Math.round(w * dpr));
    this.canvas.height = Math.max(1, Math.round(h * dpr));
    this.dpr = dpr;
    this.dirty = true;
  };

  Viewer.prototype.renderLoop = function () {
    var self = this;
    var now = Date.now();

    if (now - this.stats.lastTick >= 1000) {
      var delta = now - (this.stats.lastTick || now);
      this.stats.lastTick = now;
      this.stats.fps = delta > 0 ? (this.stats.moved.length * 1000 / delta) : 0;
      this.stats.kbps = delta > 0 ? (this.stats.bytes / 1024 * 1000 / delta) : 0;
      this.stats.moved = [];
      this.stats.bytes = 0;
      if (this.opts.onStats) this.opts.onStats(this.stats);
    }

    if (this.dirty) {
      this.dirty = false;
      this.draw();
    }

    requestAnimationFrame(function (t) { self.renderLoop(t); });
  };

  Viewer.prototype.draw = function () {
    var c = this.ctx;
    var cw = this.canvas.width, ch = this.canvas.height;
    c.fillStyle = '#000';
    c.fillRect(0, 0, cw, ch);

    if (!this.pending) return;
    var src = this.pending.canvas;          // 已是合成好的完整画面
    var iw = src.width, ih = src.height;
    if (!iw || !ih) return;

    this.lastMeta = this.pending.meta;

    var scale;
    if (this.fit) {
      scale = Math.min(cw / iw, ch / ih);
    } else {
      scale = this.dpr * this.zoom;
    }

    var dw = iw * scale, dh = ih * scale;
    var dx = (cw - dw) / 2 + this.offset[0] * this.dpr;
    var dy = (ch - dh) / 2 + this.offset[1] * this.dpr;

    c.imageSmoothingEnabled = true;
    c.imageSmoothingQuality = 'high';
    try {
      c.drawImage(src, dx, dy, dw, dh);
    } catch (e) { }
  };

  // 手势：双指缩放 / 拖动
  // ---------------- 远程控制（把本机操作转发给服务端）----------------
  //
  // 服务端支持这些消息（坐标是 0~1 的归一化坐标）：
  //   {t:"input", kind:"move",  x, y}
  //   {t:"input", kind:"down"/"up"/"click", button:"left|right|middle", x, y}
  //   {t:"input", kind:"wheel", delta: ±120, x, y}
  //   {t:"input", kind:"key",   key:"a|enter|f5…", ctrl, alt, shift, win}
  // 只有在服务端开启「允许远程控制」后才会真正生效。

  Viewer.prototype.setControl = function (on) {
    this.control = !!on;
    return this.control;
  };

  Viewer.prototype.isControl = function () {
    return !!this.control;
  };

  /// 屏幕坐标 → 归一化图像坐标（自动考虑适应/缩放/平移）
  Viewer.prototype.toNormalized = function (clientX, clientY) {
    var rect = this.canvas.getBoundingClientRect();
    var cw = this.canvas.width, ch = this.canvas.height;
    if (!this.pending || !rect.width || !cw) return null;

    var iw = this.pending.canvas.width, ih = this.pending.canvas.height;
    var scale = this.fit
      ? Math.min(cw / iw, ch / ih)
      : this.dpr * this.zoom;

    var dw = iw * scale, dh = ih * scale;
    var dx = (cw - dw) / 2 + this.offset[0] * this.dpr;
    var dy = (ch - dh) / 2 + this.offset[1] * this.dpr;

    // 把 CSS 像素换算到 canvas 像素
    var px = (clientX - rect.left) * (cw / rect.width);
    var py = (clientY - rect.top) * (ch / rect.height);

    var nx = (px - dx) / dw;
    var ny = (py - dy) / dh;
    if (nx < -0.02 || nx > 1.02 || ny < -0.02 || ny > 1.02) return null;

    return { x: Math.max(0, Math.min(1, nx)), y: Math.max(0, Math.min(1, ny)) };
  };

  Viewer.prototype.sendPointer = function (kind, p, button, delta) {
    if (!this.control || !p) return;
    var msg = { t: 'input', kind: kind, x: Math.round(p.x * 10000) / 10000, y: Math.round(p.y * 10000) / 10000 };
    if (button) msg.button = button;
    if (delta !== undefined) msg.delta = delta;
    this.send(msg);
  };

  /// 发送按键。extra 可带 ctrl/alt/shift/win。
  Viewer.prototype.sendKey = function (key, extra) {
    if (!this.control || !key) return;
    var msg = { t: 'input', kind: 'key', key: key };
    if (extra) {
      if (extra.ctrl) msg.ctrl = true;
      if (extra.alt) msg.alt = true;
      if (extra.shift) msg.shift = true;
      if (extra.win) msg.win = true;
    }
    this.send(msg);
  };

  /// 浏览器 KeyboardEvent.key → 服务端认识的键名
  var KEY_ALIAS = {
    ' ': 'space', 'Spacebar': 'space',
    'ArrowUp': 'up', 'ArrowDown': 'down', 'ArrowLeft': 'left', 'ArrowRight': 'right',
    'Enter': 'enter', 'Escape': 'esc', 'Backspace': 'backspace', 'Tab': 'tab',
    'Delete': 'delete', 'Insert': 'insert', 'Home': 'home', 'End': 'end',
    'PageUp': 'pageup', 'PageDown': 'pagedown',
    'Control': null, 'Alt': null, 'Shift': null, 'Meta': null, 'CapsLock': null
  };

  Viewer.prototype.keyName = function (ev) {
    var k = ev.key;
    if (k === undefined || k === null) return null;
    if (Object.prototype.hasOwnProperty.call(KEY_ALIAS, k)) return KEY_ALIAS[k];
    if (k.length === 1) return k;                       // 可打印字符
    if (/^F\d{1,2}$/.test(k)) return k.toLowerCase();   // F1~F24
    return null;
  };

  /// 绑定鼠标/滚轮/键盘（电脑浏览器用）
  Viewer.prototype.attachInput = function (el) {
    var self = this;
    var down = false;

    el.addEventListener('mousedown', function (e) {
      if (!self.control) return;
      var p = self.toNormalized(e.clientX, e.clientY);
      if (!p) return;
      down = true;
      var btn = e.button === 2 ? 'right' : (e.button === 1 ? 'middle' : 'left');
      self.sendPointer('down', p, btn);
      el.focus();
      e.preventDefault();
    });

    el.addEventListener('mousemove', function (e) {
      if (!self.control) return;
      var p = self.toNormalized(e.clientX, e.clientY);
      if (!p) return;
      // 按下时用 down+move 连续拖动；未按下时只报告位置
      self.sendPointer('move', p);
    });

    el.addEventListener('mouseup', function (e) {
      if (!self.control || !down) return;
      var p = self.toNormalized(e.clientX, e.clientY);
      down = false;
      if (!p) return;
      var btn = e.button === 2 ? 'right' : (e.button === 1 ? 'middle' : 'left');
      self.sendPointer('up', p, btn);
      e.preventDefault();
    });

    // 松手时鼠标可能在画布外面（HUD 上、窗口外、Alt-Tab 之后），
    // 只监听画布的 mouseup 会漏掉，对方电脑的左键就一直按着不放（拖动/框选停不下来）。
    // 所以窗口级兜底：任何地方松手、失焦、触摸取消都要把按键补上。
    function releaseAnywhere(e) {
      if (!self.control || !down) return;
      var x = e && typeof e.clientX === 'number' ? e.clientX : null;
      var y = e && typeof e.clientY === 'number' ? e.clientY : null;
      down = false;
      var p = (x === null) ? null : self.toNormalized(x, y);
      if (!p) p = self.lastPoint || null;
      if (!p) return;
      var btn = e && e.button === 2 ? 'right' : (e && e.button === 1 ? 'middle' : 'left');
      self.sendPointer('up', p, btn);
    }
    window.addEventListener('mouseup', releaseAnywhere, true);
    window.addEventListener('blur', function () { releaseAnywhere(null); });
    el.addEventListener('mouseleave', function (e) { if (down) releaseAnywhere(e); });

    el.addEventListener('contextmenu', function (e) { if (self.control) e.preventDefault(); });

    el.addEventListener('wheel', function (e) {
      if (!self.control) return;
      var p = self.toNormalized(e.clientX, e.clientY);
      if (!p) return;
      self.sendPointer('wheel', p, null, e.deltaY > 0 ? -120 : 120);
      e.preventDefault();
    }, { passive: false });

    document.addEventListener('keydown', function (e) {
      if (!self.control) return;
      // 输入用户名/密码时不抢按键
      var t = e.target;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA')) return;

      var name = self.keyName(e);
      if (!name) return;
      self.sendKey(name, { ctrl: e.ctrlKey, alt: e.altKey, shift: e.shiftKey, win: e.metaKey });
      if (e.key !== 'F5' && e.key !== 'F11' && e.key !== 'F12') e.preventDefault();
    });
  };

  Viewer.prototype.attachGestures = function (el) {
    var self = this;

    /// 结束一次单指操作：位移小=单击，位移大=拖拽（拖拽时才发左键 down/up）
    function finishSingle(st, endT) {
      if (!st || !self.control || st.singleDone) return;

      var p = endT ? self.toNormalized(endT.clientX, endT.clientY) : st.remoteStart;
      p = p || st.remoteStart;
      if (!p) return;

      if (st.dragging) {
        self.sendPointer('move', p);
        self.sendPointer('up', p, 'left');
      } else {
        // 延迟到确认是「单击」才发，避免长按右键时残留一个按住的左键
        self.sendPointer('move', p);
        self.sendPointer('down', p, 'left');
        self.sendPointer('up', p, 'left');
      }
      st.singleDone = true;
    }

    el.addEventListener('touchstart', function (e) {
      if (e.touches.length === 2) {
        var dx = e.touches[0].clientX - e.touches[1].clientX;
        var dy = e.touches[0].clientY - e.touches[1].clientY;
        self.touchState = {
          mode: 'pinch',
          dist: Math.hypot(dx, dy),
          zoom: self.zoom,
          mid: [(e.touches[0].clientX + e.touches[1].clientX) / 2,
                (e.touches[0].clientY + e.touches[1].clientY) / 2],
          // 双指：距离基本不变 = 滚动；明显变化 = 缩放
          scrollAcc: 0,
          scrollStartY: (e.touches[0].clientY + e.touches[1].clientY) / 2
        };
        e.preventDefault();
        return;
      }

      if (e.touches.length !== 1) return;

      var t0 = e.touches[0];
      var p0 = self.control ? self.toNormalized(t0.clientX, t0.clientY) : null;

      // 控制模式下必须吃掉这次触摸：不吃的话浏览器会在 touchend 之后再补一套
      // 合成的 mouse 事件，attachInput 又发一次 down/up —— 手机上点一下变成点两下，
      // 长按还会弹出系统的文字选择/右键菜单。
      if (self.control && p0) e.preventDefault();

      var st = {
        mode: 'pan',
        x: t0.clientX, y: t0.clientY,
        offset: [self.offset[0], self.offset[1]],
        moved: 0,
        remoteStart: p0,
        dragging: false,      // 是否已经进入「按住左键拖动」
        singleDone: false,    // 本次单指操作是否已处理完
        lastSent: 0,
        longPressFired: false,
        timer: null
      };
      self.touchState = st;

      // 长按 480ms 且几乎没动 = 右键单击
      if (self.control && p0) {
        st.timer = setTimeout(function () {
          if (self.touchState !== st || st.moved >= 8 || st.dragging || st.singleDone) return;

          // 按下的那一刻这个点已经通过 move 报给服务端了，这里直接补右键 down/up
          self.sendPointer('move', p0);
          self.sendPointer('down', p0, 'right');
          self.sendPointer('up', p0, 'right');

          st.longPressFired = true;
          st.singleDone = true;
          st.remoteStart = null;
          if (self.opts.onLongPress) self.opts.onLongPress();
        }, 480);
      }
    }, { passive: false });

    el.addEventListener('touchmove', function (e) {
      var st = self.touchState;
      if (!st) return;

      // ---------- 双指：滚动 或 缩放 ----------
      if (st.mode === 'pinch' && e.touches.length === 2) {
        var dx = e.touches[0].clientX - e.touches[1].clientX;
        var dy = e.touches[0].clientY - e.touches[1].clientY;
        var dist = Math.hypot(dx, dy);
        var midY = (e.touches[0].clientY + e.touches[1].clientY) / 2;
        var ratio = dist / (st.dist || 1);

        // 距离变化不大 → 当成滚动（发送滚轮）
        if (self.control && ratio > 0.85 && ratio < 1.15) {
          // 手指上滑 = 内容跟着往上走 = 滚轮向下（和触屏直觉一致）
          var deltaY = st.scrollStartY - midY;
          st.scrollAcc += deltaY;
          st.scrollStartY = midY;

          const NOTCH = 60;                          // 每 60px 算一格滚轮
          if (Math.abs(st.scrollAcc) >= NOTCH) {
            var notches = Math.trunc(st.scrollAcc / NOTCH);
            st.scrollAcc -= notches * NOTCH;
            self.send({ t: 'input', kind: 'wheel', delta: notches > 0 ? -120 : 120 });
          }
        } else {
          // 距离明显变化 → 缩放
          self.fit = false;
          self.zoom = Math.max(0.2, Math.min(6, st.zoom * ratio));
          self.dirty = true;
        }
        e.preventDefault();
        return;
      }

      // ---------- 单指 ----------
      if (st.mode === 'pan' && e.touches.length === 1) {
        var t1 = e.touches[0];
        var mx = t1.clientX - st.x;
        var my = t1.clientY - st.y;
        st.moved = Math.max(st.moved, Math.hypot(mx, my));

        if (self.control) {
          // 一动就取消长按（避免"要拖动却触发右键"）
          if (st.timer && st.moved >= 8) {
            clearTimeout(st.timer);
            st.timer = null;
          }

          if (st.singleDone) { e.preventDefault(); return; }   // 右键已发，忽略后续

          // 位移超过阈值才升级为「按住左键拖动」
          if (!st.dragging && st.moved >= 8) {
            if (st.timer) { clearTimeout(st.timer); st.timer = null; }
            st.dragging = true;
            var pd = self.toNormalized(t1.clientX, t1.clientY);
            if (pd) self.sendPointer('down', pd, 'left');
          }

          var now = Date.now();
          if (now - st.lastSent >= 16) {
            st.lastSent = now;
            var p = self.toNormalized(t1.clientX, t1.clientY);
            if (p) self.sendPointer('move', p);
          }
          e.preventDefault();
        }

        // 适应窗口时不平移（否则会跟控制模式打架）
        if (!self.fit) {
          self.offset = [st.offset[0] + mx, st.offset[1] + my];
          self.dirty = true;
          e.preventDefault();
        }
        return;
      }
    }, { passive: false });

    el.addEventListener('touchend', function (e) {
      var st = self.touchState;
      self.touchState = null;
      if (!st) return;

      if (st.timer) { clearTimeout(st.timer); st.timer = null; }

      if (st.mode === 'pan' && e.touches.length === 0) {
        var endT = e.changedTouches && e.changedTouches[0];

        if (self.control && !st.longPressFired) {
          finishSingle(st, endT);
          return;
        }

        // 非控制模式（或已发右键）：保留单击/双击切换 HUD
        // 已经发过右键的那次触摸不再算「点按」，免得右键顺带把 HUD 也切了
        if (st.moved < 8 && !st.longPressFired) {
          var now = Date.now();
          if (now - self.lastTap < 300) {
            self.lastTap = 0;
            if (self.opts.onDoubleTap) self.opts.onDoubleTap();
          } else {
            self.lastTap = now;
            if (self.opts.onTap) setTimeout(function () {
              if (self.lastTap !== 0) self.opts.onTap();
            }, 300);
          }
        }
      }
    }, { passive: true });

    // 鼠标滚轮缩放（网页版）
    el.addEventListener('wheel', function (e) {
      // 控制模式下滚轮是「操作对方电脑」，不能再顺手把本地画面缩放掉：
      // 以前电脑网页端开着 wheelZoom，滚一下本地放大一次、对方也滚一次，很难用
      if (self.control) return;
      if (!e.ctrlKey && !self.opts.wheelZoom) return;
      self.fit = false;
      self.zoom = Math.max(0.2, Math.min(6, self.zoom * (e.deltaY < 0 ? 1.1 : 0.9)));
      self.dirty = true;
      e.preventDefault();
    }, { passive: false });
  };

  Viewer.prototype.setFit = function (fit) {
    this.fit = fit;
    if (fit) { this.zoom = 1; this.offset = [0, 0]; }
    this.dirty = true;
  };

  Viewer.prototype.frameSize = function () {
    if (!this.pending) return null;
    return { w: this.pending.canvas.width, h: this.pending.canvas.height, meta: this.pending.meta };
  };

  Viewer.prototype.snapshot = function () {
    try { return this.canvas.toDataURL('image/jpeg', 0.92); } catch (e) { return null; }
  };

  window.Watching = { Viewer: Viewer, parseFrame: parseFrame, HEADER: HEADER };
})();
