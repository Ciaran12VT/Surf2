using Surf2.Models;

namespace Surf2.Services;

public interface IReferenceDefinitionParser
{
    bool CanParse(string filePath);

    IEnumerable<ReferenceEntity> Parse(string filePath, string content);
}
