import { api, type Upload, type UploadFilter, type Uploads } from '../api/client';
import { auth } from '../auth';
import { sizeOf } from '../format';
import { logs } from '@node/logs/store';
import type { Page } from '@node/router';
import { reloadButton, shell } from '@node/shell';
import { errorMessage, formatSince, formatTimestamp } from '@node/ui';
import { html, nothing, render, repeat, type TemplateResult } from '@node/view';

/**
 * Every file the charging stations uploaded, with what is known of how it
 * came - filtered by uploader and by charging station, the newest or the
 * oldest first - and each to download, to read, and to remove.
 *
 * The filters are no form: each choice asks the node at once, and is kept in
 * the address of the page, so that a list of one station's uploads can be
 * sent to somebody as a link. A new upload is drawn as it arrives, where the
 * node's event stream runs.
 */
export const uploadsPage: Page = {

    title: 'Uploads',

    render({ root, url }) {

        const content = shell(root, {
            active:    '/logServer/uploads',
            title:     'Uploads',
            subtitle:  'the log files the charging stations sent.',
            actions:   reloadButton(() => load())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayRemove   = auth.can('uploads', 'edit');
        const pageSize    = 200;

        let cancelled     = false;
        let current: Uploads | null = null;
        let take          = pageSize;
        let opened        = new Set<number>();
        let said          = '';
        let problem       = '';

        const filter: UploadFilter = {
            uploader:         url.searchParams.get('uploader')        ?? undefined,
            chargingStation:  url.searchParams.get('chargingStation') ?? undefined,
            order:            url.searchParams.get('order') === 'oldest' ? 'oldest' : 'newest'
        };


        function draw(): void {

            if (current === null)
                return;

            const uploads = current;

            render(content, html`

                <section class="card" id="uploads">

                    <h2><i class="fa-solid fa-file-arrow-up"></i> Uploads
                        <span class="chip" id="uploads-total">${uploads.total}</span>
                    </h2>

                    ${filtersView(uploads)}

                    ${uploads.uploads.length === 0
                          ? html`<p class="hint" id="no-uploads">${filter.uploader || filter.chargingStation
                                    ? 'Nothing uploaded matches the filter.'
                                    : 'Nothing was uploaded yet. The Log server page says where charging stations upload to.'}</p>`
                          : html`
                              <div class="table-scroll">
                                  <table class="records uploads">
                                      <thead>
                                          <tr>
                                              <th>
                                                  <button type="button" class="link sort" id="sort-time" title="Sort by time"
                                                          @click=${() => setFilter({ order: filter.order === 'oldest' ? 'newest' : 'oldest' })}>
                                                      Time <i class="fa-solid ${filter.order === 'oldest' ? 'fa-arrow-up' : 'fa-arrow-down'}"></i>
                                                  </button>
                                              </th>
                                              <th>Charging station</th>
                                              <th>File</th>
                                              <th class="right">Size</th>
                                              <th class="upload-protocol">Via</th>
                                              <th>Uploader</th>
                                              <th>From</th>
                                              <th></th>
                                          </tr>
                                      </thead>
                                      <tbody>
                                          ${repeat(uploads.uploads, upload => upload.id, upload => uploadRows(upload))}
                                      </tbody>
                                  </table>
                              </div>

                              ${uploads.uploads.length < uploads.total ? html`
                                  <div class="form-actions">
                                      <button type="button" class="btn small" id="more" @click=${() => { take += pageSize; void load(); }}>
                                          Show ${Math.min(pageSize, uploads.total - uploads.uploads.length)} more
                                      </button>
                                      <span class="muted">${uploads.uploads.length} of ${uploads.total} shown</span>
                                  </div>
                              ` : nothing}
                          `}

                    <div class="form-actions">
                        <span id="uploads-note"  class="form-notice" role="status">${said}</span>
                        <span id="uploads-error" class="form-error"  role="alert">${problem}</span>
                    </div>

                </section>

            `);

        }


        /** Who uploaded, of which charging station, in which order: each asks at once. */
        function filtersView(Uploads: Uploads): TemplateResult {

            // What is filtered by stays offered even where the node no longer
            // knows it - an address typed by hand - so that it can be undone.
            const uploaders  = withChosen(Uploads.uploaders,        filter.uploader);
            const stations   = withChosen(Uploads.chargingStations, filter.chargingStation);

            return html`
                <div class="upload-filters">

                    <label>Charging station
                        <select id="filter-station" @change=${(event: Event) => setFilter({ chargingStation: (event.target as HTMLSelectElement).value || undefined })}>
                            <option value="" ?selected=${!filter.chargingStation}>every one</option>
                            ${stations.map(name => html`<option value=${name} ?selected=${name === filter.chargingStation}>${name}</option>`)}
                        </select>
                    </label>

                    <label>Uploader
                        <select id="filter-uploader" @change=${(event: Event) => setFilter({ uploader: (event.target as HTMLSelectElement).value || undefined })}>
                            <option value="" ?selected=${!filter.uploader}>every one</option>
                            ${uploaders.map(name => html`<option value=${name} ?selected=${name === filter.uploader}>${name}</option>`)}
                        </select>
                    </label>

                    <label>Order
                        <select id="filter-order" @change=${(event: Event) => setFilter({ order: (event.target as HTMLSelectElement).value === 'oldest' ? 'oldest' : 'newest' })}>
                            <option value="newest" ?selected=${filter.order !== 'oldest'}>newest first</option>
                            <option value="oldest" ?selected=${filter.order === 'oldest'}>oldest first</option>
                        </select>
                    </label>

                    ${filter.uploader || filter.chargingStation ? html`
                        <button type="button" class="btn small" id="filter-clear"
                                @click=${() => setFilter({ uploader: undefined, chargingStation: undefined })}>Show all</button>
                    ` : nothing}

                </div>
            `;

        }


        /** One upload, and below it - where opened - everything else known of it. */
        function uploadRows(Upload: Upload): TemplateResult {

            const open = opened.has(Upload.id);

            return html`
                <tr data-upload="${Upload.id}" class="upload ${open ? 'opened' : ''}" @click=${(event: Event) => toggle(event, Upload.id)}>
                    <td class="upload-time" title="${Upload.timestamp}">
                        ${formatTimestamp(Upload.timestamp)} <span class="muted">${formatSince(Upload.timestamp)}</span>
                    </td>
                    <td class="upload-station">
                        <button type="button" class="link" title="Only the uploads of ${Upload.chargingStation}"
                                @click=${(event: Event) => { event.stopPropagation(); setFilter({ chargingStation: Upload.chargingStation }); }}>${Upload.chargingStation}</button>
                    </td>
                    <td class="upload-file">${Upload.fileName}</td>
                    <td class="right">${sizeOf(Upload.size)}</td>
                    <td class="upload-protocol"><span class="chip">${Upload.protocol}</span></td>
                    <td class="upload-uploader">
                        <button type="button" class="link" title="Only the uploads of ${Upload.uploader}"
                                @click=${(event: Event) => { event.stopPropagation(); setFilter({ uploader: Upload.uploader }); }}>${Upload.uploader}</button>
                    </td>
                    <td class="upload-from">${Upload.remoteAddress}</td>
                    <td class="upload-actions" @click=${(event: Event) => event.stopPropagation()}>
                        <a class="btn small" href=${api.logServer.fileURL(Upload.id, true)} target="_blank" rel="noopener" title="Read it as text">View</a>
                        <a class="btn small" href=${api.logServer.fileURL(Upload.id)} download=${Upload.fileName} title="Save it">Download</a>
                        ${mayRemove ? html`
                            <button type="button" class="btn small danger" data-remove="${Upload.id}"
                                    @click=${() => void remove(Upload)}>Remove</button>
                        ` : nothing}
                    </td>
                </tr>
                ${open ? html`
                    <tr class="upload-details" data-details="${Upload.id}">
                        <td colspan="8">
                            <div class="kv-list">
                                <div class="kv"><span class="k">Upload</span><span class="v">${Upload.id}</span></div>
                                <div class="kv"><span class="k">Kept</span><span class="v">${Upload.timestamp}</span></div>
                                <div class="kv"><span class="k">Size</span><span class="v">${Upload.size} bytes</span></div>
                                <div class="kv"><span class="k">SHA-256</span><span class="v"><code>${Upload.sha256}</code></span></div>
                                <div class="kv"><span class="k">Stored as</span><span class="v"><code>${Upload.storedAs}</code></span></div>
                                ${Upload.contentType ? html`<div class="kv"><span class="k">Content type</span><span class="v"><code>${Upload.contentType}</code></span></div>` : nothing}
                                ${Upload.userAgent   ? html`<div class="kv"><span class="k">User agent</span><span class="v"><code>${Upload.userAgent}</code></span></div>`     : nothing}
                            </div>
                        </td>
                    </tr>
                ` : nothing}
            `;

        }


        function toggle(Event: Event, Id: number): void {

            // A click that selects text is somebody copying, not opening.
            if ((window.getSelection()?.toString() ?? '').length > 0 || (Event.target as HTMLElement).closest('a, button') !== null)
                return;

            if (opened.has(Id))
                opened.delete(Id);
            else
                opened.add(Id);

            draw();

        }


        /** A filter changed: in the address of the page - replaced, not pushed - and asked of the node at once. */
        function setFilter(Change: Partial<UploadFilter>): void {

            Object.assign(filter, Change);

            take    = pageSize;
            opened  = new Set();

            const query = new URLSearchParams();

            if (filter.chargingStation)    query.set('chargingStation', filter.chargingStation);
            if (filter.uploader)           query.set('uploader',        filter.uploader);
            if (filter.order === 'oldest') query.set('order',           'oldest');

            const search = query.toString();

            history.replaceState(history.state, '', `${location.pathname}${search.length > 0 ? `?${search}` : ''}`);

            void load();

        }


        async function remove(Upload: Upload): Promise<void> {

            if (!confirm(`Remove upload ${Upload.id} - '${Upload.fileName}' of ${Upload.chargingStation} - and its file? It cannot be had back.`))
                return;

            said     = '';
            problem  = '';

            try
            {
                said = (await api.logServer.removeUpload(Upload.id)).said;
                await load(false);
            }
            catch (error)
            {
                problem = errorMessage(error);
                draw();
            }

        }


        async function load(ClearSaid = true): Promise<void> {

            if (ClearSaid) {
                said     = '';
                problem  = '';
            }

            try
            {

                const loaded = await api.logServer.uploads({ ...filter, take });

                if (cancelled)
                    return;

                current = loaded;
                draw();

            }
            catch (error)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The uploads could not be loaded: ${errorMessage(error)}</div>
                    `);
            }

        }


        // A new upload, or one removed elsewhere: asked anew - once, however
        // many arrive at a time.
        let again: ReturnType<typeof setTimeout> | null = null;

        function soon(): void {
            if (again === null)
                again = setTimeout(() => { again = null; if (!cancelled) void load(false); }, 300);
        }

        const stopUploads  = logs.onEvent<Upload>('upload',               soon);
        const stopRemoved  = logs.onEvent<{ id: number }>('uploadRemoved', soon);

        void load();

        return () => {
            cancelled = true;
            stopUploads();
            stopRemoved();
            if (again !== null)
                clearTimeout(again);
        };

    }

};


/** The names offered, with the one chosen among them where it is not. */
function withChosen(Names: string[], Chosen: string | undefined): string[] {
    return Chosen && !Names.includes(Chosen) ? [ Chosen, ...Names ] : Names;
}
