using Microsoft.JSInterop;

namespace Theatre_Timeline.Services
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
        private readonly IJSRuntime _jsInterop;

        /// <summary>
        /// Initializes a new instance of <see cref="ClipboardService"/>
        /// </summary>
        /// <param name="jsInterop"></param>
        public ClipboardService(IJSRuntime jsInterop)
        {
            _jsInterop = jsInterop;
        }

        /// <summary>
        /// Copy to clipboard.
        /// </summary>
        /// <param name="text">Text to copy.</param>
        /// <returns><see cref="Task"/>.</returns>
        public async Task CopyToClipboard(string text)
        {
            await _jsInterop.InvokeVoidAsync("navigator.clipboard.writeText", text);
        }
    }
}
