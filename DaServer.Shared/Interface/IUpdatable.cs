using System.Threading.Tasks;

namespace DaServer.Shared.Interface;

public interface IUpdatable
{ 
    Task Update(long currentMs);
}