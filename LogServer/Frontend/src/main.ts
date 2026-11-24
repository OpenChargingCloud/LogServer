import './styles/app.scss';

// FontAwesome: the CSS ends up in the extracted stylesheet, the referenced
// font files become hashed assets below /assets/.
import '@fortawesome/fontawesome-free/css/fontawesome.css';
import '@fortawesome/fontawesome-free/css/solid.css';

import { html } from '@node/view';
import { nodeMenu, startNode } from '@node/start';

import { configurationPage } from './pages/configuration';
import { logServerPage } from './pages/logServer';
import { uploadersPage } from './pages/uploaders';
import { uploadsPage } from './pages/uploads';

// What a log server has pages for of its own is the uploads - first in the
// menu, since they are what it is there for - how it takes them, at
// "/logServer", and the accounts they are uploaded under. Everything else is
// what every node has: its configuration, the name servers it asks itself,
// the time servers it checks its clock against, the SSH server - which SFTP
// is served on - the node's own certificate store and the log - see
// WWCP_Node's start.ts.
startNode({

    name:  'Log Server',
    icon:  'fa-file-lines',

    menu: [
        { path: '/logServer/uploads', label: 'Uploads', icon: 'fa-file-arrow-up', permission: [ 'uploads:read' ] },
        { path: '/logServer', label: 'Log server', icon: 'fa-file-lines', permission: [ 'uploads:read' ], children: [
            { path: '/logServer/uploaders', label: 'Uploaders', icon: 'fa-charging-station', permission: [ 'uploaders:read' ] }
        ] },
        nodeMenu.configuration([
            // The name servers it asks itself.
            nodeMenu.dns,
            nodeMenu.nts,
            nodeMenu.ssh,
            { ...nodeMenu.certificates, label: 'Trusted certificates' },
            nodeMenu.serverIdentities,
            nodeMenu.identities
        ]),
        nodeMenu.logs
    ],

    // The node's own store is what this server believes and shows: the roots
    // of the name and time servers it asks itself, and the identity its web
    // interface - and with it its upload port - shows charging stations.
    certificates: {
        title:  'Trusted certificates',
        hints: {
            believes:  html`
                What this log server believes, beside the roots of this machine: a name server or a time server it
                asks itself, with a CA of its own - or a self-signed certificate - is reached through one of these.
            `,
            presents:  html`
                What it will show a server that asks who it is, with its private key. Kept for when this log server
                connects to one that does; nothing here presents it yet.
            `
        }
    },

    // "/" is every node's: the first page of the menu the person signed in may
    // open - the uploads for whoever may read them.
    pages: {

        '/configuration':        configurationPage,
        '/logServer':            logServerPage,
        '/logServer/uploads':    uploadsPage,
        '/logServer/uploaders':  uploadersPage

    }

});
