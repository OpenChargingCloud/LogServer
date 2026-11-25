/**
 * What the log server's pages say alike of what was uploaded.
 */

/** A number of bytes as somebody reads it: "512 B", "1.4 MiB". */
export function sizeOf(Bytes: number): string {

    if (!Number.isFinite(Bytes) || Bytes < 0)
        return '-';

    if (Bytes < 1024)
        return `${Bytes} B`;

    const units = [ 'KiB', 'MiB', 'GiB', 'TiB' ];

    let value = Bytes / 1024;
    let unit  = 0;

    while (value >= 1024 && unit < units.length - 1) {
        value /= 1024;
        unit++;
    }

    return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`;

}

/** A number of seconds as somebody reads it: "45 s", "30 min", "1 h", "1 h 30 min". */
export function durationOf(Seconds: number): string {

    if (!Number.isFinite(Seconds) || Seconds < 0)
        return '-';

    if (Seconds < 60)
        return `${Math.round(Seconds)} s`;

    const minutes = Math.round(Seconds / 60);

    if (minutes < 60)
        return `${minutes} min`;

    const hours = Math.floor(minutes / 60);
    const rest  = minutes % 60;

    return rest === 0 ? `${hours} h` : `${hours} h ${rest} min`;

}

/** Where this web interface was reached at: what a charging station would be told to upload to, failing a better name. */
export function thisHost(): string {
    return typeof location !== 'undefined' && location.hostname.length > 0 ? location.hostname : 'logs.example.org';
}
