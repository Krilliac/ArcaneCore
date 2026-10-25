// The sign-in form of the public page. The server gives one answer for every reason a login fails, so this form shows one
// message for it too; the password is cleared from the field as soon as it has been sent, whatever the outcome.

import { h } from '../util.js';

export const MAX_NAME = 32;
export const MAX_PASSWORD = 128;
export const MAX_CODE = 16;

/** The message for a failed login or a transport problem. Never includes more than the server's stable code told us. */
export function loginMessage(error) {
    const code = error?.code ?? null;
    const status = error?.status ?? 0;
    if (code === 'login-failed') {
        return status === 429
            ? 'Too many attempts. Wait a minute and try again.'
            : 'That name, password or code was not accepted.';
    }
    switch (code) {
        case 'login-disabled':
            return 'Signing in is not available on this server.';
        case 'feed-unavailable':
            return 'The game server could not be reached. Try again in a moment.';
        case 'rate-limited':
            return 'Too many requests. Wait a moment and try again.';
        case 'unreachable':
            return 'This page could not reach the server.';
        case 'no-session':
        case 'bad-csrf':
            return 'Your visit expired. Reload the page and try again.';
        default:
            return error?.ref ? `Signing in failed (reference ${error.ref}).` : 'Signing in failed.';
    }
}

/** Checks what was typed before it is sent. The code is optional and sent only when given. */
export function checkLoginInput({ user, password, code }) {
    const name = String(user ?? '').trim();
    if (name.length === 0 || name.length > MAX_NAME) {
        return { ok: false, field: 'user', message: `Enter your account name (up to ${MAX_NAME} characters).` };
    }
    const secret = String(password ?? '');
    if (secret.length === 0 || secret.length > MAX_PASSWORD) {
        return { ok: false, field: 'password', message: `Enter your password (up to ${MAX_PASSWORD} characters).` };
    }
    const second = String(code ?? '').trim();
    if (second.length > MAX_CODE) {
        return { ok: false, field: 'code', message: `The code is at most ${MAX_CODE} characters.` };
    }
    return { ok: true, user: name, password: secret, code: second.length > 0 ? second : undefined };
}

/**
 * The form. `onSubmit({user, password, code})` returns a promise: resolved means signed in (the page replaces the form), a
 * rejection is shown through `loginMessage`. The form never keeps the password: the field is emptied right after the call starts.
 */
export class LoginForm {
    constructor({ onSubmit, onCancel }) {
        this.onSubmit = onSubmit;
        this.user = h('input', { class: 'input', id: 'login-user', name: 'user', type: 'text', autocomplete: 'username', spellcheck: false, attrs: { maxlength: MAX_NAME, autocapitalize: 'off' } });
        this.password = h('input', { class: 'input', id: 'login-password', name: 'password', type: 'password', autocomplete: 'current-password', attrs: { maxlength: MAX_PASSWORD } });
        this.code = h('input', { class: 'input', id: 'login-code', name: 'code', type: 'text', autocomplete: 'one-time-code', spellcheck: false, attrs: { maxlength: MAX_CODE, inputmode: 'numeric' } });
        this.error = h('div', { class: 'field-error', id: 'login-error', role: 'alert', hidden: true });
        this.submit = h('button', { class: 'button button-primary', type: 'submit', text: 'Sign in' });
        const cancel = onCancel ? h('button', { class: 'button', type: 'button', text: 'Cancel', on: { click: () => onCancel() } }) : null;
        this.element = h('form', { class: 'gate-form pub-login', attrs: { novalidate: '' }, on: { submit: (e) => this.#submit(e) } },
            h('label', { class: 'field-label', for: 'login-user' }, h('span', { class: 'field-name', text: 'Account name' })),
            this.user,
            h('label', { class: 'field-label', for: 'login-password' }, h('span', { class: 'field-name', text: 'Password' })),
            this.password,
            h('label', { class: 'field-label', for: 'login-code' }, h('span', { class: 'field-name', text: 'Code (only if your account uses one)' })),
            this.code,
            this.error,
            h('div', { class: 'pub-row' }, this.submit, cancel));
    }

    focus() {
        this.user.focus();
    }

    showError(message) {
        this.error.textContent = message;
        this.error.hidden = false;
    }

    #setBusy(busy) {
        this.submit.disabled = busy;
    }

    /** Empties every field (after a sign-in, or when the form is closed). */
    clear() {
        this.user.value = '';
        this.password.value = '';
        this.code.value = '';
        this.error.textContent = '';
        this.error.hidden = true;
    }

    async #submit(event) {
        event.preventDefault();
        this.error.hidden = true;
        const checked = checkLoginInput({ user: this.user.value, password: this.password.value, code: this.code.value });
        if (!checked.ok) {
            this.showError(checked.message);
            this[checked.field].focus();
            return;
        }
        this.password.value = '';
        this.#setBusy(true);
        try {
            await this.onSubmit({ user: checked.user, password: checked.password, code: checked.code });
            this.clear();
        } catch (error) {
            this.showError(loginMessage(error));
            this.password.focus();
        } finally {
            this.#setBusy(false);
        }
    }
}
