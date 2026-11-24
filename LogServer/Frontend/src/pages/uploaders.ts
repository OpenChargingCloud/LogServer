import { api, type Uploader, type UploadersChanged } from '../api/client';
import { auth } from '../auth';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { copyText, errorMessage, field, formatSince, formatTimestamp, whileSaving } from '@node/ui';
import { anyFormTypedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render, repeat, type TemplateResult } from '@node/view';

/**
 * The accounts files are uploaded under: accounts of the node in the
 * uploader role, one a charging station as a rule - made here with a
 * password made up for them and shown once, given a new one, and taken away.
 *
 * An uploader taken away keeps its account and what it uploaded; it may only
 * upload no more. Its SSH keys, for SFTP without a password, are given to it
 * as to any account - with 'sshKeys' at the command line.
 */
export const uploadersPage: Page = {

    title: 'Uploaders',

    render({ root }) {

        const content = shell(root, {
            active:    '/logServer/uploaders',
            title:     'Uploaders',
            subtitle:  'the accounts charging stations upload under.',
            actions:   reloadButton(() => load())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayChange = auth.can('uploaders', 'edit');

        let cancelled = false;
        let current: Uploader[] | null = null;

        /** The password just made, shown until the page is drawn anew from the node. */
        let shown: { uploader: string; password: string } | null = null;


        function draw(): void {

            if (current === null)
                return;

            render(content, html`

                ${mayChange ? nothing : html`
                    <div class="notice">
                        ${mayButNot('look at the uploaders', 'make or change them')}
                    </div>
                `}

                <div class="cards stacked">
                    ${shown !== null ? passwordCard(shown.uploader, shown.password) : nothing}
                    ${uploadersCard(current)}
                    ${addCard()}
                </div>

            `);

        }


        /** The password just made: shown this once, and never again. */
        function passwordCard(Uploader: string, Password: string): TemplateResult {

            return html`
                <section class="card" id="password-card">

                    <h2><i class="fa-solid fa-key"></i> The password of ${Uploader}</h2>

                    <p class="hint">
                        Shown this once: the log server keeps only what checks it. Give it to the charging station now -
                        as the credentials of the upload URL, or of SFTP.
                    </p>

                    <div class="password-shown">
                        <code id="password">${Password}</code>
                        <button type="button" class="btn small" id="copy-password"
                                @click=${(event: Event) => void copied(event, Password)}>Copy</button>
                        <button type="button" class="btn small" id="hide-password"
                                @click=${() => { shown = null; draw(); }}>Done</button>
                    </div>

                </section>
            `;

        }


        async function copied(Event: Event, Password: string): Promise<void> {
            const button = Event.currentTarget as HTMLButtonElement;
            button.textContent = await copyText(Password);
        }


        function uploadersCard(Uploaders: Uploader[]): TemplateResult {

            return html`
                <section class="card" id="uploaders">

                    <h2><i class="fa-solid fa-charging-station"></i> Uploaders <span class="chip">${Uploaders.length}</span></h2>

                    ${Uploaders.length === 0
                          ? html`<p class="hint" id="no-uploaders">None yet: nobody may upload. Make one below - one a charging station, named as the station is.</p>`
                          : html`
                              <div class="table-scroll">
                                  <table class="records uploaders">
                                      <thead>
                                          <tr><th>Name</th><th>Called</th><th class="right">Uploads</th><th>Last upload</th><th></th></tr>
                                      </thead>
                                      <tbody>
                                          ${repeat(Uploaders, uploader => uploader.id, uploader => uploaderRow(uploader))}
                                      </tbody>
                                  </table>
                              </div>
                          `}

                    <div class="form-actions">
                        <span id="uploaders-note"  class="form-notice" role="status"></span>
                        <span id="uploaders-error" class="form-error"  role="alert"></span>
                    </div>

                </section>
            `;

        }


        function uploaderRow(Uploader: Uploader): TemplateResult {

            return html`
                <tr data-uploader="${Uploader.id}">
                    <td><code>${Uploader.id}</code> ${Uploader.disabled ? html`<span class="chip bad">disabled</span>` : nothing}</td>
                    <td>${Uploader.name !== Uploader.id ? Uploader.name : ''}</td>
                    <td class="right">
                        ${Uploader.uploads > 0
                              ? html`<a href="logServer/uploads?uploader=${encodeURIComponent(Uploader.id)}">${Uploader.uploads}</a>`
                              : '0'}
                    </td>
                    <td>${Uploader.lastUpload !== null
                              ? html`${formatTimestamp(Uploader.lastUpload)} <span class="muted">${formatSince(Uploader.lastUpload)}</span>`
                              : html`<span class="muted">never</span>`}</td>
                    <td class="record-actions">
                        <button type="button" class="btn small" data-password="${Uploader.id}" ?disabled=${!mayChange}
                                @click=${() => void newPassword(Uploader)}>New password</button>
                        <button type="button" class="btn small danger" data-remove="${Uploader.id}" ?disabled=${!mayChange}
                                @click=${() => void remove(Uploader)}>Remove</button>
                    </td>
                </tr>
            `;

        }


        /** The form of a new uploader: the name it signs in with, and what it is called. */
        function addCard(): TemplateResult {

            return html`
                <section class="card" id="add-uploader-card">

                    <h2><i class="fa-solid fa-plus"></i> A new uploader</h2>

                    <form id="add-uploader" class="form-stack" @submit=${onAdd}>

                        <label>Name
                            <input type="text" name="id" required autocomplete="off" spellcheck="false"
                                   pattern="[A-Za-z0-9._\\-]+" maxlength="64"
                                   placeholder="CS001" ?disabled=${!mayChange} />
                            <span class="hint">
                                What it signs in with - as a rule the charging station's name, since over SFTP the station
                                is the uploader. Letters, digits, '.', '-' and '_'.
                            </span>
                        </label>

                        <label>Called
                            <input type="text" name="name" autocomplete="off"
                                   placeholder="Charging station in the yard" ?disabled=${!mayChange} />
                            <span class="hint">What it is, for whoever reads the list; its name where empty.</span>
                        </label>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ?disabled=${!mayChange}>Make the uploader</button>
                            <span id="add-uploader-note"  class="form-notice" role="status"></span>
                            <span id="add-uploader-error" class="form-error"  role="alert"></span>
                        </div>

                    </form>

                </section>
            `;

        }


        function onAdd(event: SubmitEvent): void {

            event.preventDefault();

            const form  = event.currentTarget as HTMLFormElement;
            const id    = field(form, 'id');
            const name  = field(form, 'name');

            void change(() => api.logServer.addUploader(id, name), '#add-uploader', form);

        }


        function newPassword(Uploader: Uploader): Promise<void> {

            if (!confirm(`Give ${Uploader.id} a new password? The one it has opens nothing from then on - the charging station has to be told the new one.`))
                return Promise.resolve();

            return change(() => api.logServer.newPassword(Uploader.id), '#uploaders');

        }


        function remove(Uploader: Uploader): Promise<void> {

            if (!confirm(`Take ${Uploader.id} away? It may upload no more. What it uploaded is kept.`))
                return Promise.resolve();

            return change(() => api.logServer.removeUploader(Uploader.id), '#uploaders');

        }


        /**
         * Send a change, and draw the uploaders the node has then: what it
         * made of it, not what was sent - with the password it made, once.
         */
        async function change(Send:  () => Promise<UploadersChanged>,
                              From:  '#add-uploader' | '#uploaders',
                              Form:  HTMLFormElement | null = null): Promise<void> {

            const note   = content.querySelector<HTMLElement>(`${From}-note`);
            const error  = content.querySelector<HTMLElement>(`${From}-error`);

            for (const line of content.querySelectorAll<HTMLElement>('.form-notice, .form-error'))
                line.textContent = '';

            try
            {

                const changed = await whileSaving(content, note, Send);

                current  = changed.uploaders;
                shown    = changed.password !== null && changed.uploader !== null
                               ? { uploader: changed.uploader, password: changed.password }
                               : null;

                draw();
                Form?.reset();

                const said = content.querySelector<HTMLElement>(`${From}-note`);

                if (said !== null)
                    said.textContent = changed.said;

            }
            catch (problem)
            {
                if (error !== null)
                    error.textContent = errorMessage(problem);
            }

        }


        async function load(): Promise<void> {

            try
            {

                const loaded = await api.logServer.uploaders();

                if (cancelled)
                    return;

                current  = loaded;
                shown    = null;

                // Loaded anew - Reload - is what the node has, the form too.
                draw();
                content.querySelector<HTMLFormElement>('#add-uploader')?.reset();

            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The uploaders could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        const release = unsaved.heldBy(() => anyFormTypedSinceDrawn(content));

        void load();

        return () => { cancelled = true; release(); };

    }

};
