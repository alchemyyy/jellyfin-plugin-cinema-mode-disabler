// Runs the injected script against a minimal window and localStorage: node --test Jellyfin.Plugin.CinemaModeDisabler.Tests/*.test.mjs
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';

const script = readFileSync(new URL('../Jellyfin.Plugin.CinemaModeDisabler/Web/disable-cinema-mode.js', import.meta.url), 'utf8');

function createWindow(entries = {}, { throwOnAccess = false } = {}) {
    const store = new Map(Object.entries(entries));
    const listeners = {};
    const localStorage = {
        getItem(key) {
            if (throwOnAccess) {
                throw new Error('SecurityError');
            }
            return store.has(key) ? store.get(key) : null;
        },
        setItem(key, value) {
            if (throwOnAccess) {
                throw new Error('SecurityError');
            }
            store.set(key, String(value));
        }
    };
    const window = {
        localStorage,
        addEventListener(type, listener) {
            (listeners[type] ??= []).push(listener);
        }
    };
    return { window, store, dispatch: type => (listeners[type] ?? []).forEach(listener => listener()) };
}

function run(window) {
    vm.runInNewContext(script, { window });
}

test('disables Cinema Mode for every stored user at load', () => {
    const credentials = { Servers: [{ UserId: 'a1' }, { UserId: 'b2' }, { Id: 'no-user' }, null] };
    const { window, store } = createWindow({ jellyfin_credentials: JSON.stringify(credentials), 'a1-enableCinemaMode': 'true' });

    run(window);

    assert.equal(store.get('a1-enableCinemaMode'), 'false');
    assert.equal(store.get('b2-enableCinemaMode'), 'false');
    assert.deepEqual([...store.keys()].filter(key => key.endsWith('-enableCinemaMode')).sort(), ['a1-enableCinemaMode', 'b2-enableCinemaMode']);
});

test('covers a user who logs in after load on the next hash change', () => {
    const { window, store, dispatch } = createWindow();

    run(window);
    assert.equal(store.size, 0);

    store.set('jellyfin_credentials', JSON.stringify({ Servers: [{ UserId: 'c3' }] }));
    dispatch('hashchange');

    assert.equal(store.get('c3-enableCinemaMode'), 'false');
});

test('never throws on malformed credentials or blocked storage', () => {
    for (const value of ['not json', 'null', '"text"', '{"Servers":{}}', '{"Servers":[{"UserId":5}]}']) {
        const { window, store } = createWindow({ jellyfin_credentials: value });
        assert.doesNotThrow(() => run(window));
        assert.deepEqual([...store.keys()], ['jellyfin_credentials']);
    }

    const { window } = createWindow({}, { throwOnAccess: true });
    assert.doesNotThrow(() => run(window));
});
