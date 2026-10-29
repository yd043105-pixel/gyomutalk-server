// Synthetic test accounts explicitly accept required current documents.
// Negative agreement tests use raw requests and never call this helper.
const cached=new Map();
export async function acceptRequiredPoliciesForFixture(base){
 if(!cached.has(base)){
  const response=await fetch(base+'/api/registration/policy');if(!response.ok)throw Error('Fixture policy unavailable');
  const policy=await response.json();if(!policy.canRegister)throw Error('Fixture registration disabled');
  cached.set(base,{revision:policy.revision,acceptedIds:policy.documents.filter(d=>d.required).map(d=>d.id)});
 }
 return cached.get(base);
}
