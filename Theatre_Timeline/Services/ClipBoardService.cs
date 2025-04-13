using Microsoft.JSInterop;

namespace Theatre_TimeLine.Services
{
    /// <summary>
    /// Clipboard service.
    /// </summary>
    public interface IClipboardService
    {
        Task CopyToClipboard(string text);
    }

    /// <summary>
    /// Clipboard service.
    /// </summary>
    public class ClipboardService : IClipboardService
    {
        private readonly IJSRuntime jsInterop;

        /// <summary>
        /// Initializes a new instance of <see cref="ClipboardService"/>
        /// </summary>
        /// <param name="jsInterop"></param>
        public ClipboardService(IJSRuntime jsInterop)
        {
            this.jsInterop = jsInterop;
        }

        /// <summary>
        /// Copy to clipboard.
        /// </summary>
        /// <param name="text">Text to copy.</param>
        /// <returns><see cref="Task"/>.</returns>
        public async Task CopyToClipboard(string text)
        {
            await this.jsInterop.InvokeVoidAsync("navigator.clipboard.writeText", text);
        }
    }
}
