using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microservices.Common.Exceptions;
using Microservices.ExternalServices.Authorization;
using Microservices.ExternalServices.Authorization.Types;
using Microservices.ExternalServices.Billing;
using Microservices.ExternalServices.Billing.Types;
using Microservices.ExternalServices.CatDb;
using Microservices.ExternalServices.CatDb.Types;
using Microservices.ExternalServices.CatExchange;
using Microservices.ExternalServices.CatExchange.Types;
using Microservices.ExternalServices.Database;
using Microservices.Types;

namespace Microservices
{
    public class FavouriteCats<T> : IEntityWithId<T>
    {
        public T Id { get; set; }
        public List<Guid> CatIds { get; set; }
    }
    public class CatsInfo<T> : IEntityWithId<T>
    {
        public T Id { get; set; }
        public Cat Cat { get; set; }
    }
    public class CatShelterService : ICatShelterService
    {
        public IDatabase Database { get; set; }
        public IAuthorizationService AuthorizationService { get; set; }
        public IBillingService BillingService { get; set; }
        public ICatInfoService CatInfoService { get; set; }
        public ICatExchangeService CatExchangeService { get; set; }
        public bool IsThrewConnectionException { get; set; }
        public CatShelterService(
            IDatabase database,
            IAuthorizationService authorizationService,
            IBillingService billingService,
            ICatInfoService catInfoService,
            ICatExchangeService catExchangeService)
        {
            Database = database;
            AuthorizationService = authorizationService;
            BillingService = billingService;
            CatInfoService = catInfoService;
            CatExchangeService = catExchangeService;
            IsThrewConnectionException = false;
        }

        public Task<List<Cat>> GetCatsAsync(string sessionId, int skip, int limit, CancellationToken cancellationToken)
        {
            Exception exception = new Exception();
            if(cancellationToken.IsCancellationRequested)
            {
                exception = new OperationCanceledException();
                throw exception;
            }
            AuthorizationResult authorizationResult = new AuthorizationResult();
            List<Product> billingResult = new List<Product>();
            try
            {
                authorizationResult = AuthorizationService.AuthorizeAsync(sessionId, cancellationToken).Result;
            }
            catch
            {
                exception = AuthorizationService.AuthorizeAsync(sessionId, cancellationToken).Exception;
                if(exception.GetType() is ConnectionException && !IsThrewConnectionException && AuthorizationService.AuthorizeAsync(sessionId, cancellationToken).Result.IsSuccess)
                {
                    this.IsThrewConnectionException = true;
                    GetCatsAsync(sessionId,skip,limit,cancellationToken);
                }
                else
                {
                    exception = new InternalErrorException();
                }
                throw exception;
            }
            try
            {
                Task<List<Product>> billing = BillingService.GetProductsAsync(skip, limit, cancellationToken);
                billingResult = billing.Result;
            }
            catch
            {
                exception = BillingService.GetProductsAsync(skip, limit, cancellationToken).Exception;
                if (exception.GetType() is ConnectionException && !IsThrewConnectionException)
                {
                    this.IsThrewConnectionException = true;
                    GetCatsAsync(sessionId, skip, limit, cancellationToken);
                }
                else
                {
                    exception = new InternalErrorException();
                }
                throw exception;
            }
            var breeds = new List<Guid>();
            foreach(var product in billingResult)
            {
                breeds.Add(product.BreedId);
            }
            
            
            List<CatInfo> catInfos = new List<CatInfo>();
            try
            {
                Task<CatInfo[]> catInfo = CatInfoService.FindByBreedIdAsync(breeds.ToArray(), cancellationToken);
                CatInfo[] catInfoArray = catInfo.Result;
                foreach(var catInfoItem in catInfoArray)
                {
                    catInfos.Add(catInfoItem);
                }

            }
            catch
            {
                exception = CatInfoService.FindByBreedIdAsync(breeds.ToArray(), cancellationToken).Exception;
                if (exception.GetType() is ConnectionException && !IsThrewConnectionException)
                {
                    this.IsThrewConnectionException = true;
                    GetCatsAsync(sessionId, skip, limit, cancellationToken);
                }
                else
                {
                    exception = new InternalErrorException();
                }
                throw exception;
            }
            
            
            Dictionary<Guid, CatPriceHistory> catExchangeResult = new Dictionary<Guid, CatPriceHistory>();
            try
            {
                Task<Dictionary<Guid, CatPriceHistory>> catExchange = CatExchangeService.GetPriceInfoAsync(breeds.ToArray(), cancellationToken);
                catExchangeResult = catExchange.Result;
            }
            catch
            {
                exception = CatExchangeService.GetPriceInfoAsync(breeds.ToArray(), cancellationToken).Exception;
                if (exception.GetType() is ConnectionException && !IsThrewConnectionException)
                {
                    this.IsThrewConnectionException = true;
                    GetCatsAsync(sessionId, skip, limit, cancellationToken);
                }
                else
                {
                    this.IsThrewConnectionException = true;
                    exception = new InternalErrorException();
                }
                throw exception;
            }
            List<Cat> catsResult = new List<Cat>();
            foreach(var bill in billingResult)
            {
                CatInfo info = catInfos.First(x => x.BreedId == bill.BreedId);
                CatPriceHistory history = catExchangeResult[bill.BreedId];
                List<(DateTime, decimal)> prices = new List<(DateTime, decimal)>();
                decimal lastPrice;
                foreach(var h in history.Prices)
                {
                    (DateTime, decimal) item = (h.Date,h.Price);
                    prices.Add(item);
                }

                Cat cat = new Cat() { Id = bill.Id, 
                    BreedId = bill.BreedId, 
                    Name="",
                    AddedBy = authorizationResult.UserId, 
                    CatPhoto = null, 
                    Breed = info.BreedName, 
                    BreedPhoto = info.Photo, 
                    Prices = prices,
                    Price = prices.Last().Item2};
                catsResult.Add(cat);
            }
            IDatabaseCollection<CatsInfo<Guid>, Guid> databaseCollection = Database.GetCollection<CatsInfo<Guid>, Guid>("Cats");
            CatsInfo<Guid> favourite = new CatsInfo<Guid>();
            try
            {
                for(int i=0;i<catsResult.Count;i++)
                {
                    favourite = databaseCollection.FindAsync(catsResult[i].Id, cancellationToken).Result;
                    catsResult[i].Name = favourite.Cat.Name;
                    catsResult[i].CatPhoto = favourite.Cat.CatPhoto;
                    if(catsResult[i].CatPhoto==null)
                    {
                        catsResult[i].BreedPhoto = favourite.Cat.BreedPhoto;
                    }
                    
                }
                foreach(var cat in catsResult)
                {
                    favourite = databaseCollection.FindAsync(cat.Id, cancellationToken).Result;
                    
                }
                
            }
            catch
            {
                exception = new InternalErrorException();
                throw exception;
            }
            this.IsThrewConnectionException = false;
            return Task.FromResult<List<Cat>>(catsResult);

        }

        public Task AddCatToFavouritesAsync(string sessionId, Guid catId, CancellationToken cancellationToken)
        {
            Exception exception = new Exception();
            if (cancellationToken.IsCancellationRequested)
            {
                exception = new OperationCanceledException();
                throw exception;
            }
            AuthorizationResult authorizationResult = new AuthorizationResult();
            Product billingResult = new Product();
            try
            {
                authorizationResult = AuthorizationService.AuthorizeAsync(sessionId, cancellationToken).Result;
            }
            catch
            {
                exception = AuthorizationService.AuthorizeAsync(sessionId, cancellationToken).Exception;
                if (exception.GetType() is ConnectionException && !IsThrewConnectionException && AuthorizationService.AuthorizeAsync(sessionId, cancellationToken).Result.IsSuccess)
                {
                    this.IsThrewConnectionException = true;
                    BuyCatAsync(sessionId, catId, cancellationToken);
                }
                else
                {
                    exception = new InternalErrorException();
                }
                throw exception;
            }
            try
            {
                Task<Product> billing = BillingService.GetProductAsync(catId, cancellationToken);
                billingResult = billing.Result;
            }
            catch
            {
                exception = BillingService.GetProductAsync(catId, cancellationToken).Exception;
                if (exception.GetType() is ConnectionException && !IsThrewConnectionException)
                {
                    this.IsThrewConnectionException = true;
                    BuyCatAsync(sessionId, catId, cancellationToken);
                }
                else
                {
                    exception = new InternalErrorException();
                }
                throw exception;
            }
            
            IDatabaseCollection<FavouriteCats<Guid>,Guid> databaseCollection = Database.GetCollection<FavouriteCats<Guid>, Guid>("FavouriteCats");

            try
            {
                FavouriteCats<Guid> favourite = databaseCollection.FindAsync(authorizationResult.UserId,cancellationToken).Result;
                    List<Guid> guids = favourite.CatIds;
                    guids.Add(catId);
                    favourite.CatIds = guids;
                    databaseCollection.WriteAsync(favourite, cancellationToken);
                
            }
            catch
            {
                List<Guid> guids = new List<Guid>() { catId };
                var favourite = new FavouriteCats<Guid>() { Id = authorizationResult.UserId, CatIds = guids };
                databaseCollection.WriteAsync(favourite, cancellationToken);

            }
            this.IsThrewConnectionException = false;
            return Task.CompletedTask;

        }

        public Task<List<Cat>> GetFavouriteCatsAsync(string sessionId, CancellationToken cancellationToken)
        {
            Exception exception = new Exception();
            List<Cat> catsResult = new List<Cat>();

            if (cancellationToken.IsCancellationRequested)
            {
                exception = new OperationCanceledException();
                throw exception;
            }
            AuthorizationResult authorizationResult = new AuthorizationResult();
            Product billingResult = new Product();
            try
            {
                authorizationResult = AuthorizationService.AuthorizeAsync(sessionId, cancellationToken).Result;
            }
            catch
            {
                exception = AuthorizationService.AuthorizeAsync(sessionId, cancellationToken).Exception;
                if (exception.GetType() is ConnectionException && !IsThrewConnectionException && AuthorizationService.AuthorizeAsync(sessionId, cancellationToken).Result.IsSuccess)
                {
                    this.IsThrewConnectionException = true;
                    GetFavouriteCatsAsync(sessionId, cancellationToken);
                }
                else
                {
                    exception = new InternalErrorException();
                }
                throw exception;
            }
            IDatabaseCollection<FavouriteCats<Guid>, Guid> databaseCollection = Database.GetCollection<FavouriteCats<Guid>, Guid>("FavouriteCats");
            FavouriteCats<Guid> favourite = new FavouriteCats<Guid>();
            try
            {
                favourite = databaseCollection.FindAsync(authorizationResult.UserId, cancellationToken).Result;
                if(favourite==null)
                {
                    return Task.FromResult<List<Cat>>(catsResult);
                }
            }
            catch
            {
                exception = new InternalErrorException();
                throw exception;
            }
            List<Product> products = new List<Product>();
            foreach(var catId in favourite.CatIds)
            {
                   try
                   {
                       Task<Product> billing = BillingService.GetProductAsync(catId, cancellationToken);
                       billingResult = billing.Result;
                       products.Add(billingResult);
                   }
                   catch
                   {
                       exception = BillingService.GetProductAsync(catId, cancellationToken).Exception;
                       if (exception.GetType() is ConnectionException && !IsThrewConnectionException)
                       {
                           this.IsThrewConnectionException = true;
                            GetFavouriteCatsAsync(sessionId, cancellationToken);
                       }
                       else
                       {
                           exception = new InternalErrorException();
                       }
                       throw exception;
                   }       
            }
            var breeds = new List<Guid>();
            foreach (var product in products)
            {
                breeds.Add(product.BreedId);
            }
            List<CatInfo> catInfos = new List<CatInfo>();
            try
            {
                Task<CatInfo[]> catInfo = CatInfoService.FindByBreedIdAsync(breeds.ToArray(), cancellationToken);
                CatInfo[] catInfoArray = catInfo.Result;
                foreach (var catInfoItem in catInfoArray)
                {
                    catInfos.Add(catInfoItem);
                }

            }
            catch
            {
                exception = CatInfoService.FindByBreedIdAsync(breeds.ToArray(), cancellationToken).Exception;
                if (exception.GetType() is ConnectionException && !IsThrewConnectionException)
                {
                    this.IsThrewConnectionException = true;
                    GetFavouriteCatsAsync(sessionId, cancellationToken);
                }
                else
                {
                    exception = new InternalErrorException();
                }
                throw exception;
            }


            Dictionary<Guid, CatPriceHistory> catExchangeResult = new Dictionary<Guid, CatPriceHistory>();
            try
            {
                Task<Dictionary<Guid, CatPriceHistory>> catExchange = CatExchangeService.GetPriceInfoAsync(breeds.ToArray(), cancellationToken);
                catExchangeResult = catExchange.Result;
            }
            catch
            {
                exception = CatExchangeService.GetPriceInfoAsync(breeds.ToArray(), cancellationToken).Exception;
                if (exception.GetType() is ConnectionException && !IsThrewConnectionException)
                {
                    this.IsThrewConnectionException = true;
                    GetFavouriteCatsAsync(sessionId, cancellationToken);
                }
                else
                {

                    exception = new InternalErrorException();
                }
                throw exception;
            }
            foreach (var product in products)
            {
                CatInfo info = catInfos.First(x => x.BreedId == product.BreedId);
                CatPriceHistory history = catExchangeResult[product.BreedId];
                List<(DateTime, decimal)> prices = new List<(DateTime, decimal)>();
                foreach (var h in history.Prices)
                {
                    (DateTime, decimal) item = (h.Date, h.Price);
                    prices.Add(item);
                }

                Cat cat = new Cat()
                {
                    Id = product.Id,
                    BreedId = product.BreedId,
                    Name = "",
                    AddedBy = authorizationResult.UserId,
                    CatPhoto = null,
                    Breed = info.BreedName,
                    BreedPhoto = info.Photo,
                    Prices = prices,
                    Price = prices.Last().Item2
                };
                catsResult.Add(cat);
            }
            this.IsThrewConnectionException = false;
            return Task.FromResult<List<Cat>>(catsResult);
        }

        public Task DeleteCatFromFavouritesAsync(string sessionId, Guid catId, CancellationToken cancellationToken)
        {
            Exception exception = new Exception();
            if (cancellationToken.IsCancellationRequested)
            {
                exception = new OperationCanceledException();
                throw exception;
            }
            AuthorizationResult authorizationResult = new AuthorizationResult();
            Product billingResult = new Product();
            try
            {
                authorizationResult = AuthorizationService.AuthorizeAsync(sessionId, cancellationToken).Result;
            }
            catch
            {
                exception = AuthorizationService.AuthorizeAsync(sessionId, cancellationToken).Exception;
                if (exception.GetType() is ConnectionException && !IsThrewConnectionException && AuthorizationService.AuthorizeAsync(sessionId, cancellationToken).Result.IsSuccess)
                {
                    this.IsThrewConnectionException = true;
                    BuyCatAsync(sessionId, catId, cancellationToken);
                }
                else
                {
                    exception = new InternalErrorException();
                }
                throw exception;
            }
            try
            {
                Task<Product> billing = BillingService.GetProductAsync(catId, cancellationToken);
                billingResult = billing.Result;
            }
            catch
            {
                exception = BillingService.GetProductAsync(catId, cancellationToken).Exception;
                if (exception.GetType() is ConnectionException && !IsThrewConnectionException)
                {
                    this.IsThrewConnectionException = true;
                    BuyCatAsync(sessionId, catId, cancellationToken);
                }
                else
                {
                    exception = new InternalErrorException();
                }
                throw exception;
            }

            IDatabaseCollection<FavouriteCats<Guid>, Guid> databaseCollection = Database.GetCollection<FavouriteCats<Guid>, Guid>("FavouriteCats");

            try
            {
                databaseCollection.DeleteAsync(catId,cancellationToken);
            }
            catch
            {
                exception = new InternalErrorException();
                throw exception;
            }
            this.IsThrewConnectionException = false;
            return Task.CompletedTask;
        }

        public Task<Bill> BuyCatAsync(string sessionId, Guid catId, CancellationToken cancellationToken)
        {
            Exception exception = new Exception();
            if (cancellationToken.IsCancellationRequested)
            {
                exception = new OperationCanceledException();
                throw exception;
            }
            AuthorizationResult authorizationResult = new AuthorizationResult();
            Product billingResult = new Product();
            try
            {
                authorizationResult = AuthorizationService.AuthorizeAsync(sessionId, cancellationToken).Result;
            }
            catch
            {
                exception = AuthorizationService.AuthorizeAsync(sessionId, cancellationToken).Exception;
                if (exception.GetType() is ConnectionException && !IsThrewConnectionException && AuthorizationService.AuthorizeAsync(sessionId, cancellationToken).Result.IsSuccess)
                {
                    this.IsThrewConnectionException = true;
                    BuyCatAsync(sessionId, catId, cancellationToken);
                }
                else
                {
                    exception = new InternalErrorException();
                }
                throw exception;
            }
            try
            {
                Task<Product> billing = BillingService.GetProductAsync(catId, cancellationToken);
                billingResult = billing.Result;
            }
            catch
            {
                exception = BillingService.GetProductAsync(catId, cancellationToken).Exception;
                if (exception.GetType() is ConnectionException && !IsThrewConnectionException)
                {
                    this.IsThrewConnectionException = true;
                    BuyCatAsync(sessionId, catId, cancellationToken);
                }
                else
                {
                    exception = new InternalErrorException();
                }
                throw exception;
            }
            CatPriceHistory catExchangeResult = new CatPriceHistory();
            try
            {
                Task<CatPriceHistory> catExchange = CatExchangeService.GetPriceInfoAsync(billingResult.Id, cancellationToken);
                catExchangeResult = catExchange.Result;
            }
            catch
            {
                exception = CatExchangeService.GetPriceInfoAsync(billingResult.Id, cancellationToken).Exception;
                if (exception.GetType() is ConnectionException && !IsThrewConnectionException)
                {
                    this.IsThrewConnectionException = true;
                    BuyCatAsync(sessionId, catId, cancellationToken);
                }
                else
                { 
                    exception = new InternalErrorException();
                }
                throw exception;
            }
            Bill bill = new Bill() { Id = authorizationResult.UserId,ProductId=billingResult.Id, Price = catExchangeResult.Prices.Last().Price};
            this.IsThrewConnectionException = false;
            return Task.FromResult<Bill>(bill);
            // <returns>Счёт на покупку котика. Если другой пользователь получил счёт, но не оплатил его, возвращает новый счёт</returns>

        }

        public Task<Guid> AddCatAsync(string sessionId, AddCatRequest request, CancellationToken cancellationToken)
        {
            Exception exception = new Exception();
            if (cancellationToken.IsCancellationRequested)
            {
                exception = new OperationCanceledException();
                throw exception;
            }
            AuthorizationResult authorizationResult = new AuthorizationResult();
            
            try
            {
                authorizationResult = AuthorizationService.AuthorizeAsync(sessionId, cancellationToken).Result;
            }
            catch
            {
                exception = AuthorizationService.AuthorizeAsync(sessionId, cancellationToken).Exception;
                if (exception.GetType() is ConnectionException && !IsThrewConnectionException && AuthorizationService.AuthorizeAsync(sessionId, cancellationToken).Result.IsSuccess)
                {
                    this.IsThrewConnectionException = true;
                    AddCatAsync(sessionId, request, cancellationToken);
                }
                else
                {
                    exception = new InternalErrorException();
                }
                throw exception;
            }
            CatInfo catInfo = new CatInfo();
            try
            {
                Task<CatInfo> catInfoServ = CatInfoService.FindByBreedNameAsync(request.Breed, cancellationToken);
                catInfo = catInfoServ.Result;
            }
            catch
            {
                exception = CatInfoService.FindByBreedNameAsync(request.Breed, cancellationToken).Exception;
                if (exception.GetType() is ConnectionException && !IsThrewConnectionException)
                {
                    this.IsThrewConnectionException = true;
                    GetFavouriteCatsAsync(sessionId, cancellationToken);
                }
                else
                {
                    exception = new InternalErrorException();
                }
                throw exception;
            }

            CatPriceHistory catExchangeResult = new CatPriceHistory();
            try
            {
                Task<CatPriceHistory> catExchange = CatExchangeService.GetPriceInfoAsync(catInfo.BreedId, cancellationToken);
                catExchangeResult = catExchange.Result;
            }
            catch
            {
                exception = CatExchangeService.GetPriceInfoAsync(catInfo.BreedId, cancellationToken).Exception;
                if (exception.GetType() is ConnectionException && !IsThrewConnectionException)
                {
                    this.IsThrewConnectionException = true;
                    GetFavouriteCatsAsync(sessionId, cancellationToken);
                }
                else
                {

                    exception = new InternalErrorException();
                }
                throw exception;
            }
            List<(DateTime, decimal)> prices = new List<(DateTime, decimal)>();
            foreach (var h in catExchangeResult.Prices)
            {
                (DateTime, decimal) item = (h.Date, h.Price);
                prices.Add(item);
            }
            Cat cat = new Cat() { Name = request.Name, 
                Breed = request.Breed, 
                CatPhoto = request.Photo, 
                AddedBy = authorizationResult.UserId, 
                BreedId = catInfo.BreedId, 
                BreedPhoto = catInfo.Photo, 
                Id = Guid.NewGuid(), 
                Prices = null,
                Price = 1000};
            if(prices.Count>0)
            {
                cat.Prices = prices;
                cat.Price = prices.Last().Item2;
            }
            IDatabaseCollection<CatsInfo<Guid>, Guid> databaseCollection = Database.GetCollection<CatsInfo<Guid>, Guid>("Cats");

            try
            {
                CatsInfo<Guid> cats = new CatsInfo<Guid>() { Id = cat.Id, Cat = cat};
                var result = databaseCollection.FindAsync(cats.Id,cancellationToken);
                if(result!=null)
                {
                    exception = new InternalErrorException();
                    throw exception;
                }
                databaseCollection.WriteAsync(cats, cancellationToken);
            }
            catch
            {
                exception = new InternalErrorException();
                throw exception;

            }
            Product product = new Product() { BreedId = cat.BreedId, Id = cat.Id};
            try
            {
                BillingService.AddProductAsync(product, cancellationToken);
            }
            catch
            {
                exception = BillingService.AddProductAsync(product, cancellationToken).Exception;
                if (exception.GetType() is ConnectionException && !IsThrewConnectionException)
                {
                    this.IsThrewConnectionException = true;
                    BuyCatAsync(sessionId, cat.Id, cancellationToken);
                }
                else
                {
                    exception = new InternalErrorException();
                }
                throw exception;
            }
            this.IsThrewConnectionException = false;
            return Task.FromResult<Guid>(product.Id);
        }
    }
}