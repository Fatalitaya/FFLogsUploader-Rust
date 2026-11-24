var window = {
    location: { search: '?id=1&gameContentDetectionEnabled=true&metersEnabled=false&liveFightDataEnabled=false' },
    addEventListener: function(type, listener) { if (type === 'message') { this._messageListener = listener; } },
    removeEventListener: function() {},
    dispatchEvent: function() { return true; },
    _messageListener: null
};
var document = {
    createElement: function() { return { style: {}, appendChild: function(){}, addEventListener: function(){} }; },
    getElementsByTagName: function() { return [{ appendChild: function(){} }]; },
    getElementById: function() { return null; },
    querySelector: function() { return null; },
    querySelectorAll: function() { return []; },
    body: { appendChild: function(){} },
    head: { appendChild: function(){} },
    readyState: 'complete',
    addEventListener: function() {},
    removeEventListener: function() {}
};
var self = window;
var navigator = { userAgent: 'FFLogsPlugin/1.0', platform: 'Win32' };
var location = window.location;
var localStorage = { getItem: function() { return null; }, setItem: function() {}, removeItem: function() {} };
var sessionStorage = localStorage;

var _timerId = 0;
var _pendingTimeouts = [];
function setTimeout(fn, delay) {
    var id = ++_timerId;
    if (typeof fn === 'function') _pendingTimeouts.push(fn);
    return id;
}
function setInterval() { return ++_timerId; }
function clearTimeout() {}
function clearInterval() {}
var performance = { now: function() { return Date.now(); } };
function fetch() { throw new Error('fetch not supported'); }

// Drain deferred callbacks — called after each parser message
function __drainTimeouts() {
    var safety = 1000;
    while (_pendingTimeouts.length > 0 && safety-- > 0) {
        var batch = _pendingTimeouts.splice(0, _pendingTimeouts.length);
        for (var i = 0; i < batch.length; i++) {
            try { batch[i](); } catch(e) {}
        }
    }
}

var URLSearchParams = function(init) {
    this._params = {};
    if (typeof init === 'string') {
        var str = init.charAt(0) === '?' ? init.substring(1) : init;
        var pairs = str.split('&');
        for (var i = 0; i < pairs.length; i++) {
            var kv = pairs[i].split('=');
            if (kv.length === 2) this._params[decodeURIComponent(kv[0])] = decodeURIComponent(kv[1]);
        }
    }
};
URLSearchParams.prototype.get = function(k) { return this._params.hasOwnProperty(k) ? this._params[k] : null; };
URLSearchParams.prototype.set = function(k, v) { this._params[k] = v; };
URLSearchParams.prototype.has = function(k) { return this._params.hasOwnProperty(k); };
