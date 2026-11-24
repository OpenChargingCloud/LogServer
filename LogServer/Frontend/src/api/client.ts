import { apiURL,
         nodeAPI,
         request,
         type Certificate        as NodeCertificate,
         type CertificateImport  as NodeCertificateImport,
         type CertificateStore   as NodeCertificateStore,
         type NodeConfiguration,
         type NodeMe,
         type NodeResource,
         type NodeStatus,
         type Operation }  from '@node/api/client';


// What every node answers - the log, name resolution, the time, the store,
// who is signed in - and how it is asked are WWCP_Node's, and every page here
// reads them from this module. What follows is what a log server says of its
// own: its resources, its configuration's own section, the kinds its store
// keeps - and the uploads, below "/logServer".
export * from '@node/api/client';


/**
 * What a role may be allowed to touch on this log server: what every node
 * has, and the uploads - read, listed and downloaded; edited, removed - the
 * uploaders - read, listed; edited, made, given a new password, taken away -
 * and uploading itself, which charging stations run.
 */
export type Resource = NodeResource | 'uploads' | 'uploaders' | 'upload';

/** What somebody signed in to this log server may do: an operation on a resource, written "uploads:edit". */
export type Permission = `${Resource}:${Operation}`;

/** Who is signed in to the web interface. */
export type Me = NodeMe<Resource>;

/** How the node is doing right now: what every node says. */
export type Status = NodeStatus;

/**
 * What the log server is made of: every node's sections, and its own. Only
 * the shape the Configuration page relies on is named; the fields of each
 * section are drawn from whatever the server sends.
 */
export interface Configuration extends NodeConfiguration {
    logServer:  Record<string, unknown>;
}

/** What a certificate in the node's own store is for: the four kinds of TLS. */
export type CertificateKind = 'tlsRoot' | 'clientRoot' | 'tlsServer' | 'tlsIdentity';

/** One certificate in the node's own store. */
export type Certificate = NodeCertificate<CertificateKind>;

/** What an import into the node's own store sends. */
export type CertificateImport = NodeCertificateImport<CertificateKind>;

/** The node's own store, grouped the way it is shown. */
export type CertificateStore = NodeCertificateStore<CertificateKind>;


/** How a file arrived. */
export type UploadProtocol = 'HTTP' | 'SFTP';

/** One file uploaded, and what is known about how it came. */
export interface Upload {
    /** Its number: one above the one before, never reused. */
    id:               number;
    /** When it was complete and kept - "2026-11-24T10:15:00.123Z". */
    timestamp:        string;
    protocol:         UploadProtocol;
    /** The account it was uploaded under, or "anonymous". */
    uploader:         string;
    /** The charging station it is from: as the path of an HTTP upload names it, the uploader otherwise. */
    chargingStation:  string;
    /** The name it was uploaded as. */
    fileName:         string;
    /** Where it is kept, below the uploads directory. */
    storedAs:         string;
    /** In bytes. */
    size:             number;
    /** In lower-case hex. */
    sha256:           string;
    /** Where it came from. */
    remoteAddress:    string;
    /** What an HTTP upload said it was. */
    contentType?:     string;
    /** What an HTTP upload said it was sent with. */
    userAgent?:       string;
}

/** Which uploads are asked for: GET /logServer/uploads. */
export interface UploadFilter {
    uploader?:         string;
    chargingStation?:  string;
    /** ISO 8601: at or after. */
    since?:            string;
    /** ISO 8601: before. */
    until?:            string;
    order?:            'newest' | 'oldest';
    skip?:             number;
    take?:             number;
}

/** What GET /logServer/uploads answers. */
export interface Uploads {
    /** How many match the filter in all. */
    total:             number;
    /** Those of them asked for, in the order asked for. */
    uploads:           Upload[];
    /** Every account that ever uploaded - for the filter to offer. */
    uploaders:         string[];
    /** Every charging station ever uploaded of - for the filter to offer. */
    chargingStations:  string[];
}

/** How the log server stands: GET /logServer. */
export interface LogServerStatus {
    /** The directory the uploads are kept in. */
    directory:         string;
    /** The port HTTP uploads are taken on, or null where they are not. */
    http:              { listening: boolean; url: string; tls: boolean; anonymous: boolean; refused: number } | null;
    /** SFTP on the SSH server, or null where uploads are not taken over it. */
    sftp:              { serving: boolean; port: number | null; path: string; passwords: boolean } | null;
    /** In bytes. */
    maxFileSize:       number;
    uploads:           number;
    /** In bytes. */
    totalSize:         number;
    uploaders:         number;
    chargingStations:  number;
    latest:            Upload | null;
}

/** The "logServer" section as it is in effect - the switches of this start included. */
export interface LogServerSettings {
    directory:      string;
    http:           boolean;
    httpListen:     string;
    httpPort:       number;
    anonymousHTTP:  boolean;
    sftp:           boolean;
    maxFileSize:    number;
}

/** An account in the uploader role. */
export interface Uploader {
    id:          string;
    name:        string;
    disabled:    boolean;
    uploads:     number;
    lastUpload:  string | null;
}

/** What a change of the uploaders answers: what it said, the password where one was made - once - and the uploaders as they are now. */
export interface UploadersChanged {
    said:       string;
    uploader:   string | null;
    password:   string | null;
    uploaders:  Uploader[];
}


/** The query string of a filter: only what it says. */
function queryOf(Filter: UploadFilter): string {

    const query = new URLSearchParams();

    for (const [key, value] of Object.entries(Filter))
        if (value !== undefined && value !== null && value !== '')
            query.set(key, String(value));

    const text = query.toString();

    return text.length > 0 ? `?${text}` : '';

}


/** How the log server is asked: the routes every node has, typed with the server's own of them, and the uploads. */
export const api = {

    ...nodeAPI<{ me: Me; status: Status; configuration: Configuration; kind: CertificateKind; store: CertificateStore }>(),

    logServer: {

        status:          ()                      => request<LogServerStatus>   ('GET',  '/logServer'),
        settings:        ()                      => request<LogServerSettings> ('GET',  '/logServer/settings'),
        uploads:         (Filter: UploadFilter)  => request<Uploads>           ('GET',  '/logServer/uploads' + queryOf(Filter)),
        removeUpload:    (Id: number)            => request<{ said: string }>  ('POST', `/logServer/uploads/${Id}/remove`, {}),

        /** Where the file of an upload is: to save, or with Inline to read in the browser as text. */
        fileURL:         (Id: number, Inline = false) => apiURL(`/logServer/uploads/${Id}/file${Inline ? '?inline=true' : ''}`),

        uploaders:       ()                          => request<Uploader[]>        ('GET',  '/logServer/uploaders'),
        addUploader:     (Id: string, Name?: string) => request<UploadersChanged>  ('POST', '/logServer/uploaders', Name !== undefined && Name.length > 0 ? { id: Id, name: Name } : { id: Id }),
        newPassword:     (Id: string)                => request<UploadersChanged>  ('POST', '/logServer/uploaders/password', { id: Id }),
        removeUploader:  (Id: string)                => request<UploadersChanged>  ('POST', '/logServer/uploaders/remove', { id: Id })

    }

};
