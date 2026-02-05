using System.Collections;

namespace Features.Drawing.Domain.Interface
{
    public interface IRendererInitializer
    {
        IEnumerator InitializeAsync();
    }
}
