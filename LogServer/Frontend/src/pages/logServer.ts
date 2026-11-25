import { api, type LogServerStatus } from '../api/client';
import { durationOf, sizeOf, thisHost } from '../format';
import type { Page } from '@node/router';
import { reloadButton, shell } from '@node/shell';
import { errorMessage, formatSince, formatTimestamp } from '@node/ui';
import { html, nothing, render, type TemplateResult } from '@node/view';

/**
 * How the log server takes uploads - by HTTP PUT on a port of its own, and
 * over SFTP on the SSH server - with what a charging station is told to
 * upload to, and what it keeps.
 *
 * Read-only: where uploads are taken is the "logServer" section's to say, and
 * who may upload the Uploaders page's.
 */
export const logServerPage: Page = {

    title: 'Log server',

    render({ root }) {

        const content = shell(root, {
            active:    '/logServer',
            title:     'Log server',
            subtitle:  'how the log files of charging stations arrive here.',
            actions:   reloadButton(() => load())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        let cancelled = false;


        function httpCard(Status: LogServerStatus): TemplateResult {

            const http = Status.http;

            if (http === null)
                return html`
                    <section class="card" id="http-uploads">
                        <h2><i class="fa-solid fa-upload"></i> HTTP uploads <span class="chip">off</span></h2>
                        <p class="hint">The "logServer" section says "http": false: nothing is taken by HTTP PUT.</p>
                    </section>
                `;

            // What charging stations are told: "publicURL", or the log server's
            // guess - the same the banner says.
            const url          = http.url;
            const behindProxy  = !http.tls && url.startsWith('https:');
            const certificate  = http.certificate;

            return html`
                <section class="card" id="http-uploads">

                    <h2><i class="fa-solid fa-upload"></i> HTTP uploads
                        <span class="chip ${http.listening ? 'ok' : 'bad'}">${http.listening ? 'listening' : 'not listening'}</span>
                        ${http.tls          ? html`<span class="chip on">TLS</span>`
                        : behindProxy       ? html`<span class="chip">TLS at a proxy</span>`
                        :                     html`<span class="chip warn">plain HTTP</span>`}
                    </h2>

                    <div class="kv-list">
                        <div class="kv"><span class="k">Upload to</span><span class="v"><code>${url}&lt;charging station&gt;/&lt;file name&gt;</code>
                            ${http.publicURL === null ? html` <span class="muted">(guessed - "publicURL" says it)</span>` : nothing}</span></div>
                        <div class="kv"><span class="k">Listening on</span><span class="v"><code>${http.listen}</code></span></div>
                        ${certificate === null ? nothing : html`
                            <div class="kv"><span class="k">Certificate</span><span class="v">${certificate.shows === null
                                ? html`<strong>none to show</strong> - every connection is refused: ${certificate.problem}`
                                : html`${certificate.shows}, until ${formatTimestamp(certificate.notAfter ?? '')},
                                       <span class="muted">from ${certificate.certificate === 'store'
                                           ? `the certificate store${certificate.label !== null ? ` ('${certificate.label}')` : ''}`
                                           : certificate.fullchain}</span>`}</span></div>
                        `}
                        <div class="kv"><span class="k">Signed in</span><span class="v">${http.anonymous
                            ? html`with the credentials of an uploader - or <strong>without any</strong>: "anonymousHTTP" is true`
                            : 'with the credentials of an uploader, by HTTP Basic Auth'}</span></div>
                        <div class="kv"><span class="k">Largest file</span><span class="v">${sizeOf(Status.maxFileSize)}, written to the disk as it arrives</span></div>
                        <div class="kv"><span class="k">Longest upload</span><span class="v">${durationOf(http.timeout)}</span></div>
                        <div class="kv"><span class="k">Refused</span><span class="v">${http.refused} since the start</span></div>
                    </div>

                    <p class="hint">
                        What a charging station is given as the location of OCPP's GetLog or GetDiagnostics - with the
                        credentials of its account in it, and the station in the path:
                    </p>
                    <pre class="ask-with">${url.replace('://', '://CS001:<password>@')}CS001/</pre>

                    <p class="hint">Or, to try it by hand:</p>
                    <pre class="ask-with">curl -u CS001:&lt;password&gt; -T diagnostics.zip ${url}CS001/diagnostics.zip</pre>

                    ${http.tls || behindProxy ? nothing : html`
                        <p class="hint warn-line">
                            Over plain HTTP the file and the password cross the network in the clear. Give the upload
                            port a certificate of its own ("logServer.tls"), or put a proxy that speaks TLS in front
                            and say its "https" URL in "logServer.publicURL".
                        </p>
                    `}

                </section>
            `;

        }


        function sftpCard(Status: LogServerStatus): TemplateResult {

            const sftp = Status.sftp;

            if (sftp === null)
                return html`
                    <section class="card" id="sftp-uploads">
                        <h2><i class="fa-solid fa-terminal"></i> SFTP uploads <span class="chip">off</span></h2>
                        <p class="hint">The "logServer" section says "sftp": false: nothing is taken over SFTP.</p>
                    </section>
                `;

            const port = sftp.port ?? 22;

            return html`
                <section class="card" id="sftp-uploads">

                    <h2><i class="fa-solid fa-terminal"></i> SFTP uploads
                        <span class="chip ${sftp.serving ? 'ok' : 'bad'}">${sftp.serving ? 'served' : 'not served'}</span>
                    </h2>

                    <div class="kv-list">
                        <div class="kv"><span class="k">Upload to</span><span class="v"><code>sftp://${thisHost()}:${port}${sftp.path}/</code></span></div>
                        <div class="kv"><span class="k">Signed in</span><span class="v">${sftp.passwords
                            ? 'as an uploader, with its password or one of its SSH keys'
                            : html`as an uploader, with one of its SSH keys - passwords open nothing, since the "ssh" section does not say "passwords": true`}</span></div>
                        <div class="kv"><span class="k">Largest file</span><span class="v">${sizeOf(Status.maxFileSize)}</span></div>
                    </div>

                    ${sftp.serving ? nothing : html`
                        <p class="hint warn-line">
                            The node serves no SFTP: its SSH server is off, or the "ssh" section says "sftp": false. See SSH server.
                        </p>
                    `}

                    <p class="hint">
                        The charging station is the uploader it signs in as. Every file put in ${sftp.path} is taken in
                        at once, so that the next one of the same name never takes its place - but for one put as
                        "&lt;name&gt;.part", which is taken in when it is renamed to "&lt;name&gt;" after its last byte.
                    </p>
                    <pre class="ask-with">sftp -P ${port} CS001@${thisHost()}
sftp&gt; put diagnostics.zip ${sftp.path}/</pre>

                </section>
            `;

        }


        function keptCard(Status: LogServerStatus): TemplateResult {

            const latest = Status.latest;

            return html`
                <section class="card" id="kept">

                    <h2><i class="fa-solid fa-box-archive"></i> Kept</h2>

                    <div class="kv-list">
                        <div class="kv"><span class="k">Uploads</span><span class="v">${Status.uploads} <span class="muted">(${sizeOf(Status.totalSize)})</span></span></div>
                        <div class="kv"><span class="k">Charging stations</span><span class="v">${Status.chargingStations}</span></div>
                        <div class="kv"><span class="k">Uploaders</span><span class="v">${Status.uploaders} that uploaded</span></div>
                        <div class="kv"><span class="k">Directory</span><span class="v"><code>${Status.directory}</code></span></div>
                        <div class="kv"><span class="k">Newest</span><span class="v">${latest === null
                            ? 'nothing uploaded yet'
                            : html`'${latest.fileName}' of ${latest.chargingStation}, ${sizeOf(latest.size)} by ${latest.protocol},
                                   ${formatTimestamp(latest.timestamp)} <span class="muted">(${formatSince(latest.timestamp)})</span>`}</span></div>
                    </div>

                    <p class="hint"><a href="logServer/uploads">All uploads</a></p>

                </section>
            `;

        }


        async function load(): Promise<void> {

            try
            {

                const status = await api.logServer.status();

                if (cancelled)
                    return;

                render(content, html`
                    <div class="cards">
                        ${keptCard(status)}
                        ${httpCard(status)}
                        ${sftpCard(status)}
                    </div>
                `);

            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The log server could not be asked: ${errorMessage(problem)}</div>
                    `);
            }

        }

        void load();

        return () => { cancelled = true; };

    }

};
